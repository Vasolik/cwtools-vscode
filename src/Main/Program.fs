﻿module Main.Program

open LSP
open LSP.Types
open System
open System.IO
open Microsoft.FSharp.Compiler.SourceCodeServices
open CWTools.Parser
open CWTools.Parser.Types
open CWTools.Common
open CWTools.Common.STLConstants
open CWTools.Games
open FParsec
open System.Threading.Tasks
open System.Text
open System.Reflection
open System.IO
open System.Runtime.InteropServices
open FSharp.Data
open LSP
open CWTools.Validation.ValidationCore
open System
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler
open CWTools.Validation.Rules
open System.Xml.Schema
open CWTools.Games.Files
let private TODO() = raise (Exception "TODO")

type LintRequestMsg =
    | UpdateRequest of VersionedTextDocumentIdentifier
    | WorkComplete of unit


type Server(send : BinaryWriter) =
    let send = send
    let docs = DocumentStore()
    let projects = ProjectManager()
    let checker = FSharpChecker.Create()
    let emptyProjectOptions = checker.GetProjectOptionsFromCommandLineArgs("NotFound.fsproj", [||])
    let notFound (doc: Uri) (): 'Any =
        raise (Exception (sprintf "%s does not exist" (doc.ToString())))
    let mutable docErrors : DocumentHighlight list = []

    let mutable gameObj : option<STLGame> = None
    let mutable languages : Lang list = []
    let mutable rootUri : Uri option = None
    let mutable validateVanilla : bool = false
    let mutable experimental : bool = false

    let mutable ignoreCodes : string list = []
    let mutable ignoreFiles : string list = []
    let mutable experimental_completion : bool = false
    let (|TrySuccess|TryFailure|) tryResult =
        match tryResult with
        | true, value -> TrySuccess value
        | _ -> TryFailure
    let sevToDiagSev =
        function
        | Severity.Error -> DiagnosticSeverity.Error
        | Severity.Warning -> DiagnosticSeverity.Warning
        | Severity.Information -> DiagnosticSeverity.Information
        | Severity.Hint -> DiagnosticSeverity.Hint
    let parserErrorToDiagnostics e =
        let code, sev, file, error, (position : range), length = e
        let startC, endC = match length with
        | 0 -> 0,( int position.StartColumn) - 1
        | x ->(int position.StartColumn) - 1,(int position.StartColumn) + length - 1
        let result = {
                        range = {
                                start = {
                                        line = (int position.StartLine - 1)
                                        character = startC
                                    }
                                ``end`` = {
                                            line = (int position.StartLine - 1)
                                            character = endC
                                    }
                        }
                        severity = Some (sevToDiagSev sev)
                        code = Some code
                        source = Some code
                        message = error
                    }
        (file, result)

    let sendDiagnostics s =
        let diagnosticFilter (f, d) =
            match (f, d) with
            | _, {code = Some code} when List.contains code ignoreCodes -> false
            | f, _ when List.contains (Path.GetFileName f) ignoreFiles -> false
            | _, _ -> true
        s |>  List.groupBy fst
            |> List.map ((fun (f, rs) -> f, rs |> List.filter (diagnosticFilter)) >>
                (fun (f, rs) ->
                    try PublishDiagnostics {uri = (match Uri.TryCreate(f, UriKind.Absolute) with |TrySuccess value -> value |TryFailure -> eprintfn "%s" f; Uri "/") ; diagnostics = List.map snd rs} with |e -> failwith (sprintf "%A" rs)))
            |> List.iter (fun f -> LanguageServer.sendNotification send f)

    let lint (doc: Uri) (shallowAnalyze : bool) (forceDisk : bool) : Async<unit> =
        async {
            let name =
                if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && doc.LocalPath.StartsWith "/"
                then doc.LocalPath.Substring(1)
                else doc.LocalPath
            let filetext = if forceDisk then None else docs.GetText doc
            let getRange (start: FParsec.Position) (endp : FParsec.Position) = mkRange start.StreamName (mkPos (int start.Line) (int start.Column)) (mkPos (int endp.Line) (int endp.Column))
            let parserErrors =
                match docs.GetText doc with
                |None -> []
                |Some t ->
                    let parsed = CKParser.parseString t name
                    match name, parsed with
                    | x, _ when x.EndsWith(".yml") -> []
                    | _, Success(_,_,_) -> []
                    | _, Failure(msg,p,s) -> [("CW001", Severity.Error, name, msg, (getRange p.Position p.Position), 0)]
            let errors =
                match shallowAnalyze with
                |true -> parserErrors
                |false ->
                    parserErrors @
                    match gameObj with
                        |None -> []
                        |Some game ->
                            let results = game.UpdateFile name filetext
                            results |> List.map (fun (c, s, n, l, e, _) -> (c, s, n.FileName, e, n, l) )
            match errors with
            | [] -> LanguageServer.sendNotification send (PublishDiagnostics {uri = doc; diagnostics = []})
            | x -> x
                    |> List.map parserErrorToDiagnostics
                    |> sendDiagnostics
            //let compilerOptions = projects.FindProjectOptions doc |> Option.defaultValue emptyCompilerOptions
            // let! parseResults, checkAnswer = checker.ParseAndCheckFileInProject(name, version, source, projectOptions)
            // for error in parseResults.Errors do
            //     eprintfn "%s %d:%d %s" error.FileName error.StartLineAlternate error.StartColumn error.Message
            // match checkAnswer with
            // | FSharpCheckFileAnswer.Aborted -> eprintfn "Aborted checking %s" name
            // | FSharpCheckFileAnswer.Succeeded checkResults ->
            //     for error in checkResults.Errors do
            //         eprintfn "%s %d:%d %s" error.FileName error.StartLineAlternate error.StartColumn error.Message
        }

    let lintAgent =
        MailboxProcessor.Start(
            (fun agent ->
            let analyzeTask uri =
                new Task(
                    fun () ->
                    try
                        try
                            lint uri false false |> Async.RunSynchronously
                        with
                        | e -> eprintfn "uri %A \n exception %A" uri.LocalPath e
                    finally
                        agent.Post (WorkComplete ()))
            let analyze (file : VersionedTextDocumentIdentifier) =
                //eprintfn "Analyze %s" (file.uri.ToString())
                let task = analyzeTask file.uri
                //let task = new Task((fun () -> lint (file.uri) false false |> Async.RunSynchronously; agent.Post (WorkComplete ())))
                task.Start()
            let rec loop (inprogress : bool) (state : Map<string, VersionedTextDocumentIdentifier>) =
                async{
                    let! msg = agent.Receive()
                    match msg, inprogress with
                    | UpdateRequest (ur), false ->
                        analyze ur
                        return! loop true state
                    | UpdateRequest (ur), true ->
                        if Map.containsKey ur.uri.LocalPath state
                        then
                            if (Map.find ur.uri.LocalPath state) |> (fun {VersionedTextDocumentIdentifier.version = v} -> v < ur.version)
                            then
                                return! loop inprogress (state |> Map.add ur.uri.LocalPath ur)
                            else
                                return! loop inprogress state
                        else
                            return! loop inprogress (state |> Map.add ur.uri.LocalPath ur)
                    | WorkComplete _, _ ->
                        if Map.isEmpty state
                        then
                            return! loop false state
                        else
                            let key, next = state |> Map.pick (fun k v -> (k, v) |> function | (k, v) -> Some (k, v))
                            let newstate = state |> Map.remove key
                            analyze next
                            return! loop true newstate
                }
            loop false Map.empty
            )
        )


    let rec replaceFirst predicate value = function
        | [] -> []
        | h :: t when predicate h -> value :: t
        | h :: t -> h :: replaceFirst predicate value t

    let fixEmbeddedFileName (s : string) =
        let count = (Seq.filter ((=) '.') >> Seq.length) s
        let mutable out = "//" + s
        [1 .. count - 1] |> List.iter (fun _ -> out <- (replaceFirst ((=) '.') '\\' (out |> List.ofSeq)) |> Array.ofList |> String )
        out

    let rec getAllFolders dirs =
        if Seq.isEmpty dirs then Seq.empty else
            seq { yield! dirs |> Seq.collect Directory.EnumerateDirectories
                  yield! dirs |> Seq.collect Directory.EnumerateDirectories |> getAllFolders }
    let getAllFoldersUnion dirs =
        seq {
            yield! dirs
            yield! getAllFolders dirs
        }

    let processWorkspace (uri : option<Uri>) =
        LanguageServer.sendNotification send (LoadingBar {value = "Loading project..."; enable = true})
        match uri with
        |Some u ->
            let path =
                if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && u.LocalPath.StartsWith "/"
                then u.LocalPath.Substring(1)
                else u.LocalPath
            try
                eprintfn "%s" path
                let filelist = Assembly.GetEntryAssembly().GetManifestResourceStream("Main.files.vanilla_files_2.1.0.csv")
                                |> (fun f -> (new StreamReader(f)).ReadToEnd().Split(Environment.NewLine))
                                |> Array.toList |> List.map (fun f -> f, "")
                let docspath = "Main.files.trigger_docs_2.1.0.txt"
                let docs = DocsParser.parseDocsStream (Assembly.GetEntryAssembly().GetManifestResourceStream(docspath))
                let embeddedFileNames = Assembly.GetEntryAssembly().GetManifestResourceNames() |> Array.filter (fun f -> f.Contains("common") || f.Contains("localisation") || f.Contains("interface") || f.Contains("events") || f.Contains("gfx"))
                let embeddedFiles = embeddedFileNames |> List.ofArray |> List.map (fun f -> fixEmbeddedFileName f, (new StreamReader(Assembly.GetEntryAssembly().GetManifestResourceStream(f))).ReadToEnd())

               // let docs = DocsParser.parseDocsFile @"G:\Projects\CK2 Events\CWTools\files\game_effects_triggers_1.9.1.txt"
                let triggers, effects = (docs |> (function |Success(p, _, _) -> DocsParser.processDocs p))
                let logspath = "Main.files.setup.log"
                let modfile = SetupLogParser.parseLogsStream (Assembly.GetEntryAssembly().GetManifestResourceStream(logspath))

                let embeddedConfigFileNames = Assembly.GetEntryAssembly().GetManifestResourceNames() |> Array.filter (fun f -> f.Contains("config.config") && f.EndsWith(".cwt"))
                let embeddedConfigFiles = embeddedConfigFileNames |> List.ofArray |> List.map (fun f -> fixEmbeddedFileName f, (new StreamReader(Assembly.GetEntryAssembly().GetManifestResourceStream(f))).ReadToEnd())
                let modifiers = (modfile |> (function |Success(p, _, _) -> SetupLogParser.processLogs p))
                let configpath = "Main.files.config.cwt"
                let configFiles = (if Directory.Exists "./.cwtools" then getAllFoldersUnion (["./.cwtools"] |> Seq.ofList) else Seq.empty) |> Seq.collect (Directory.EnumerateFiles)
                let configFiles = configFiles |> List.ofSeq |> List.filter (fun f -> Path.GetExtension f = ".cwt")
                let configs =
                    match experimental_completion, configFiles.Length > 0 with
                    |false, _ -> []
                    |_, true ->
                        configFiles |> List.map (fun f -> f, File.ReadAllText(f))
                        //["./config.cwt", File.ReadAllText("./config.cwt")]
                    |_, false ->
                        embeddedConfigFiles
                //let configs = [
                eprintfn "%A" languages
                let game = STLGame(path, FilesScope.All, "", triggers, effects, modifiers, embeddedFiles @ filelist, configs, languages, validateVanilla, experimental, experimental_completion)
                gameObj <- Some game
                let getRange (start: FParsec.Position) (endp : FParsec.Position) = mkRange start.StreamName (mkPos (int start.Line) (int start.Column)) (mkPos (int endp.Line) (int endp.Column))
                let parserErrors = game.ParserErrors |> List.map (fun ( n, e, p) -> "CW001", Severity.Error, n, e, (getRange p p), 0)
                parserErrors
                    |> List.map parserErrorToDiagnostics
                    |> sendDiagnostics

                LanguageServer.sendNotification send (LoadingBar {value = "Validating files..."; enable = true})
                //eprintfn "%A" game.AllFiles
                let valErrors = game.ValidationErrors |> List.map (fun (c, s, n, l, e, _) -> (c, s, n.FileName, e, n, l) )
                let locErrors = game.LocalisationErrors() |> List.map (fun (c, s, n, l, e, _) -> (c, s, n.FileName, e, n, l) )

                valErrors @ locErrors
                    |> List.map parserErrorToDiagnostics
                    |> sendDiagnostics
                GC.Collect()
                //eprintfn "%A" game.ValidationErrors
                    // |> List.groupBy fst
                    // |> List.map ((fun (f, rs) -> f, rs |> List.filter (fun (_, d) -> match d.code with |Some s -> not (List.contains s ignoreCodes) |None -> true)) >>
                    //     (fun (f, rs) -> PublishDiagnostics {uri = (match Uri.TryCreate(f, UriKind.Absolute) with |TrySuccess value -> value |TryFailure -> eprintfn "%s" f; Uri "/") ; diagnostics = List.map snd rs}))
                    // |> List.iter (fun f -> LanguageServer.sendNotification send f)
            with
                | :? System.Exception as e -> eprintfn "%A" e

        |None -> ()
        LanguageServer.sendNotification send (LoadingBar {value = ""; enable = false})

    let hoverDocument (doc :Uri, pos: LSP.Types.Position) =
        async {
            eprintfn "Hover before word"
            let! word = LanguageServer.sendRequest send (GetWordRangeAtPosition {position = pos})
            eprintfn "Hover after word"
            return match gameObj with
            |Some game ->
                let position = Pos.fromZ pos.line pos.character// |> (fun p -> Pos.fromZ)
                let path =
                    let u = doc
                    if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && u.LocalPath.StartsWith "/"
                    then u.LocalPath.Substring(1)
                    else u.LocalPath
                let scopeContext = game.ScopesAtPos position (path) (docs.GetText doc |> Option.defaultValue "")
                let allEffects = game.ScriptedEffects @ game.ScripteTriggers
                eprintfn "Looking for effect %s in the %i effects loaded" word (allEffects.Length)
                let unescapedword = word.Replace("\\\"", "\"").Trim('"')
                let hovered = allEffects |> List.tryFind (fun e -> e.Name = unescapedword)
                let lochover = game.References.Localisation |> List.tryFind (fun (k, v) -> k = unescapedword)
                let scopesExtra = if scopeContext.IsNone then "" else
                    let scopes = scopeContext.Value
                    let header = "| Context | Scope |\n| ----- | -----|\n"
                    let root = sprintf "| ROOT | %s |\n" (scopes.Root.ToString())
                    let prevs = scopes.Scopes |> List.mapi (fun i s -> "| " + (if i = 0 then "THIS" else (String.replicate (i) "PREV")) + " | " + (s.ToString()) + " |\n") |> String.concat ""
                    let froms = scopes.From |> List.mapi (fun i s -> "| " + (String.replicate (i+1) "FROM") + " | " + (s.ToString()) + " |\n") |> String.concat ""
                    header + root + prevs + froms

                match hovered, lochover with
                |Some effect, _ ->
                    match effect with
                    | :? DocEffect as de ->
                        let scopes = String.Join(", ", de.Scopes |> List.map (fun f -> f.ToString()))
                        let content = String.Join("\n***\n",["_"+de.Desc+"_"; "Supports scopes: " + scopes; scopesExtra]) // TODO: usageeffect.Usage])
                        {contents = (MarkupContent ("markdown", content)) ; range = None}
                    | e ->
                        let scopes = String.Join(", ", e.Scopes |> List.map (fun f -> f.ToString()))
                        let content = String.Join("\n***\n",["_"+e.Name+"_"; "Supports scopes: " + scopes; scopesExtra]) // TODO: usageeffect.Usage])
                        {contents = (MarkupContent ("markdown", content)) ; range = None}
                |None, Some (_, loc) ->
                    {contents = MarkupContent ("markdown", loc.desc + "\n***\n" + scopesExtra); range = None}
                |None, None ->
                    {contents = MarkupContent ("markdown", scopesExtra); range = None}
            |_ -> {contents = MarkupContent ("markdown", ""); range = None}
        }

    let completionResolveItem (item :CompletionItem) =
        async {
            eprintfn "Completion resolve"
            return match gameObj with
                    |Some game ->
                        let allEffects = game.ScriptedEffects @ game.ScripteTriggers
                        let hovered = allEffects |> List.tryFind (fun e -> e.Name = item.label)
                        match hovered with
                        |Some effect ->
                            match effect with
                            | :? DocEffect as de ->
                                let desc = "_" + de.Desc.Replace("_", "\\_") + "_"
                                let scopes = "Supports scopes: " + String.Join(", ", de.Scopes |> List.map (fun f -> f.ToString()))
                                let usage = de.Usage
                                let content = String.Join("\n***\n",[desc; scopes; usage]) // TODO: usageeffect.Usage])
                                //{item with documentation = (MarkupContent ("markdown", content))}
                                {item with documentation = Some (DocMarkup ("markdown", content))}
                            | :? ScriptedEffect as se ->
                                let desc = se.Name.Replace("_", "\\_")
                                let comments = se.Comments.Replace("_", "\\_")
                                let scopes = "Supports scopes: " + String.Join(", ", se.Scopes |> List.map (fun f -> f.ToString()))
                                let content = String.Join("\n***\n",[desc; comments; scopes]) // TODO: usageeffect.Usage])
                                {item with documentation = Some (DocMarkup ("markdown", content))}
                            | e ->
                                let desc = "_" + e.Name.Replace("_", "\\_") + "_"
                                let scopes = "Supports scopes: " + String.Join(", ", e.Scopes |> List.map (fun f -> f.ToString()))
                                let content = String.Join("\n***\n",[desc; scopes]) // TODO: usageeffect.Usage])
                                {item with documentation = Some (DocMarkup ("markdown", content))}
                        |None -> item
                    |None -> item
        }
    let isPositionInRange (pos : range) (range : LSP.Types.Range) =
        int pos.StartColumn - 1 >= range.start.character
        && int pos.StartColumn - 1 <= range.``end``.character
        && int pos.StartLine - 1 >= range.start.line
        && int pos.StartLine - 1 <= range.``end``.line


    interface ILanguageServer with
        member this.Initialize(p: InitializeParams): InitializeResult =
            rootUri <- p.rootUri
            { capabilities =
                { defaultServerCapabilities with
                    hoverProvider = true
                    textDocumentSync =
                        { defaultTextDocumentSyncOptions with
                            openClose = true
                            willSave = true
                            save = Some { includeText = true }
                            change = TextDocumentSyncKind.Full }
                    completionProvider = Some {resolveProvider = true; triggerCharacters = []}
                    codeActionProvider = true
                    executeCommandProvider = Some {commands = ["genlocfile"; "genlocall"; "outputerrors"]} } }
        member this.Initialized(): unit =
            ()
        member this.Shutdown(): unit =
            ()
        member this.DidChangeConfiguration(p: DidChangeConfigurationParams): unit =
            let newLanguages =
                match p.settings.Item("cwtools").Item("localisation").Item("languages") with
                | JsonValue.Array o ->
                    o |> Array.choose (function |JsonValue.String s -> (match STLLang.TryParse<STLLang> s with |TrySuccess s -> Some s |TryFailure -> None) |_ -> None)
                      |> List.ofArray
                      |> (fun l ->  if List.isEmpty l then [STLLang.English] else l)
                | _ -> [STLLang.English]
            languages <- newLanguages |> List.map STL
            let newVanillaOnly =
                match p.settings.Item("cwtools").Item("errors").Item("vanilla") with
                | JsonValue.Boolean b -> b
                | _ -> false
            validateVanilla <- newVanillaOnly
            let newExperimental =
                match p.settings.Item("cwtools").Item("experimental") with
                | JsonValue.Boolean b -> b
                | _ -> false
            experimental <- newExperimental
            let newcompletion =
                match p.settings.Item("cwtools").Item("experimental_completion") with
                | JsonValue.Boolean b -> b
                | _ -> false
            experimental_completion <- newcompletion
            let newIgnoreCodes =
                match p.settings.Item("cwtools").Item("errors").Item("ignore") with
                | JsonValue.Array o ->
                    o |> Array.choose (function |JsonValue.String s -> Some s |_ -> None)
                      |> List.ofArray
                | _ -> []
            ignoreCodes <- newIgnoreCodes
            let newIgnoreFiles =
                match p.settings.Item("cwtools").Item("errors").Item("ignorefiles") with
                | JsonValue.Array o ->
                    o |> Array.choose (function |JsonValue.String s -> Some s |_ -> None)
                      |> List.ofArray
                | _ -> []
            ignoreFiles <- newIgnoreFiles
            eprintfn "New configuration %s" (p.ToString())
            let task = new Task((fun () -> processWorkspace(rootUri)))
            task.Start()

        member this.DidOpenTextDocument(p: DidOpenTextDocumentParams): unit =
            docs.Open p
            lintAgent.Post (UpdateRequest {uri = p.textDocument.uri; version = p.textDocument.version})
        member this.DidChangeTextDocument(p: DidChangeTextDocumentParams): unit =
            docs.Change p
            lintAgent.Post (UpdateRequest {uri = p.textDocument.uri; version = p.textDocument.version})
        member this.WillSaveTextDocument(p: WillSaveTextDocumentParams): unit = ()
            //lintAgent.Post (UpdateRequest {uri = p.textDocument.uri; version = p.textDocument})
        member this.WillSaveWaitUntilTextDocument(p: WillSaveTextDocumentParams): list<TextEdit> = TODO()
        member this.DidSaveTextDocument(p: DidSaveTextDocumentParams): unit =
            lintAgent.Post (UpdateRequest {uri = p.textDocument.uri; version = 0})
        member this.DidCloseTextDocument(p: DidCloseTextDocumentParams): unit =
            docs.Close p
        member this.DidChangeWatchedFiles(p: DidChangeWatchedFilesParams): unit =
            for change in p.changes do
                match change._type with
                |FileChangeType.Created ->

                    lintAgent.Post (UpdateRequest {uri = change.uri; version = 0})
                    //eprintfn "Watched file %s %s" (change.uri.ToString()) (change._type.ToString())
                |FileChangeType.Deleted ->
                    LanguageServer.sendNotification send (PublishDiagnostics {uri = change.uri; diagnostics = []})
                |_ ->                 ()
                // if change.uri.AbsolutePath.EndsWith ".fsproj" then
                //     projects.UpdateProjectFile change.uri
                    //lintAgent.Post (UpdateRequest {uri = p.textDocument.uri; version = p.textDocument.version})
        member this.Completion(p: TextDocumentPositionParams): CompletionList =
            let defaultCompletionItem = { label = ""; additionalTextEdits = None; kind = None; detail = None; documentation = None; sortText = None; filterText = None; insertText = None; insertTextFormat = None; textEdit = None; commitCharacters = None; command = None; data = None}
            match gameObj with
            |Some game ->
                match experimental_completion with
                |true ->
                    let position = Pos.fromZ p.position.line p.position.character// |> (fun p -> Pos.fromZ)
                    let path =
                        let u = p.textDocument.uri
                        if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && u.LocalPath.StartsWith "/"
                        then u.LocalPath.Substring(1)
                        else u.LocalPath
                    let comp = game.Complete position (path) (docs.GetText p.textDocument.uri |> Option.defaultValue "")
                    // let extraKeywords = ["yes"; "no";]
                    // let eventIDs = game.References.EventIDs
                    // let names = eventIDs @ game.References.TriggerNames @ game.References.EffectNames @ game.References.ModifierNames @ game.References.ScopeNames @ extraKeywords
                    let items =
                        comp |> List.map (
                            function
                            |Simple e -> {defaultCompletionItem with label = e}
                            |Detailed (l, d) -> {defaultCompletionItem with label = l; documentation = d |> Option.map (fun d -> DocString d)}
                            |Snippet (l, e, d) -> {defaultCompletionItem with label = l; insertText = Some e; insertTextFormat = Some InsertTextFormat.Snippet; documentation = d |> Option.map (fun d -> DocString d)})
                    // let variables = game.References.ScriptVariableNames |> List.map (fun v -> {defaultCompletionItem with label = v; kind = Some CompletionItemKind.Variable })
                    {isIncomplete = false; items = items}
                |false ->
                    let extraKeywords = ["yes"; "no";]
                    let eventIDs = game.References.EventIDs
                    let names = eventIDs @ game.References.TriggerNames @ game.References.EffectNames @ game.References.ModifierNames @ game.References.ScopeNames @ extraKeywords
                    let variables = game.References.ScriptVariableNames |> List.map (fun v -> {defaultCompletionItem with label = v; kind = Some CompletionItemKind.Variable })
                    let items = names |> List.map (fun n -> {defaultCompletionItem with label = n})
                    {isIncomplete = false; items = items @ variables}
            |None -> {isIncomplete = false; items = []}
        member this.Hover(p: TextDocumentPositionParams): Hover =
            eprintfn "Hover"
            hoverDocument (p.textDocument.uri, p.position) |> Async.RunSynchronously

        member this.ResolveCompletionItem(p: CompletionItem): CompletionItem =
            completionResolveItem(p) |> Async.RunSynchronously
        member this.SignatureHelp(p: TextDocumentPositionParams): SignatureHelp = TODO()
        member this.GotoDefinition(p: TextDocumentPositionParams): list<Location> = TODO()
        member this.FindReferences(p: ReferenceParams): list<Location> = TODO()
        member this.DocumentHighlight(p: TextDocumentPositionParams): list<DocumentHighlight> = TODO()
        member this.DocumentSymbols(p: DocumentSymbolParams): list<SymbolInformation> = TODO()
        member this.WorkspaceSymbols(p: WorkspaceSymbolParams): list<SymbolInformation> = TODO()
        member this.CodeActions(p: CodeActionParams): list<Command> =
            match gameObj with
            |Some game ->
                let es = game.LocalisationErrors()
                let les = es |> List.filter (fun (_, e, pos,_, _, _) -> (pos) |> (fun a -> (isPositionInRange a p.range) && a.FileName.Replace("\\","/") = p.textDocument.uri.LocalPath.Substring(1)) )
                match les with
                |[] -> []
                |_ ->
                    [
                        {title = "Generate localisation .yml for this file"; command = "genlocfile"; arguments = [p.textDocument.uri.LocalPath.Substring(1) |> JsonValue.String]}
                        {title = "Generate localisation .yml for all"; command = "genlocall"; arguments = []}
                    ]
            |None -> []
        member this.CodeLens(p: CodeLensParams): List<CodeLens> = TODO()
        member this.ResolveCodeLens(p: CodeLens): CodeLens = TODO()
        member this.DocumentLink(p: DocumentLinkParams): list<DocumentLink> = TODO()
        member this.ResolveDocumentLink(p: DocumentLink): DocumentLink = TODO()
        member this.DocumentFormatting(p: DocumentFormattingParams): list<TextEdit> = TODO()
        member this.DocumentRangeFormatting(p: DocumentRangeFormattingParams): list<TextEdit> = TODO()
        member this.DocumentOnTypeFormatting(p: DocumentOnTypeFormattingParams): list<TextEdit> = TODO()
        member this.Rename(p: RenameParams): WorkspaceEdit = TODO()
        member this.ExecuteCommand(p: ExecuteCommandParams): unit =
            match gameObj with
            |Some game ->
                match p with
                | {command = "genlocfile"; arguments = x::_} ->
                    let les = game.LocalisationErrors() |> List.filter (fun (_, e, pos,_, _, _) -> (pos) |> (fun a -> a.FileName.Replace("\\","/") = x.AsString()))
                    let keys = les |> List.sortBy (fun (_, _, p, _, _, _) -> (p.FileName, p.StartLine))
                                   |> List.choose (fun (_, _, _, _, _, k) -> k)
                                   |> List.map (sprintf " %s:0 \"REPLACE_ME\"")
                                   |> List.distinct
                    let text = String.Join(Environment.NewLine,keys)
                    let notif = CreateVirtualFile { uri = Uri "cwtools://1"; fileContent = text }
                    LanguageServer.sendNotification send notif
                | {command = "genlocall"; arguments = _} ->
                    let les = game.LocalisationErrors()
                    let keys = les |> List.sortBy (fun (_, _, p, _, _, _) -> (p.FileName, p.StartLine))
                                   |> List.choose (fun (_, _, _, _, _, k) -> k)
                                   |> List.map (sprintf " %s:0 \"REPLACE_ME\"")
                                   |> List.distinct
                    let text = String.Join(Environment.NewLine,keys)
                    let notif = CreateVirtualFile { uri = Uri "cwtools://1"; fileContent = text }
                    LanguageServer.sendNotification send notif
                | {command = "outputerrors"; arguments = _} ->
                    let errors = game.LocalisationErrors() @ game.ValidationErrors
                    let texts = errors |> List.map (fun (code, sev, pos, _, error, _) -> sprintf "%s, %O, %O, %s, %O, \"%s\"" pos.FileName pos.StartLine pos.StartColumn code sev error)
                    let text = String.Join(Environment.NewLine, (texts))
                    let notif = CreateVirtualFile { uri = Uri "cwtools://errors.csv"; fileContent = text }
                    LanguageServer.sendNotification send notif
                |_ -> ()
            |None -> ()


[<EntryPoint>]
let main (argv: array<string>): int =
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    let read = new BinaryReader(Console.OpenStandardInput())
    let write = new BinaryWriter(Console.OpenStandardOutput())
    let server = Server(write)
    eprintfn "Listening on stdin"
    LanguageServer.connect server read write
    0 // return an integer exit code
    //eprintfn "%A" (JsonValue.Parse "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":12660,\"rootUri\": \"file:///c%3A/Users/Thomas/Documents/Paradox%20Interactive/Stellaris\"},\"capabilities\":{\"workspace\":{}}}")
    //0
