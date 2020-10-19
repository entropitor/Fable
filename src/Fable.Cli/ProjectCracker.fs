/// This module gets the F# compiler arguments from .fsproj as well as some
/// Fable-specific tasks like tracking the sources of Fable Nuget packages
/// using Paket .paket.resolved file
module Fable.Cli.ProjectCracker

open System
open System.Xml.Linq
open System.Collections.Generic
open FSharp.Compiler.SourceCodeServices
open Fable
open Globbing.Operators

type FablePackage = Fable.Transforms.State.Package

type CrackerOptions(fableLib, define, exclude, replace, forcePkgs, projFile, optimize) =
    let builtDlls = HashSet()
    member _.FableLib: string option = fableLib
    member _.Define: string[] = define
    member _.Exclude: string option = exclude
    member _.Replace: Map<string, string> = replace
    member _.ForcePkgs: bool = forcePkgs
    member _.ProjFile: string = projFile
    member _.Optimize: bool = optimize
    member _.BuildDll(normalizedDllPath: string) =
        if not(builtDlls.Contains(normalizedDllPath)) then
            let projDir =
                normalizedDllPath.Split('/')
                |> Array.rev
                |> Array.skipWhile (fun part -> part <> "bin")
                |> Array.skip 1
                |> Array.rev
                |> String.concat "/"
            Process.runSync projDir "dotnet" ["build"] |> ignore
            builtDlls.Add(normalizedDllPath) |> ignore

type CrackerResponse =
    { FableLibDir: string
      FableLibReset: bool
      Packages: FablePackage list
      ProjectOptions: FSharpProjectOptions }

let isSystemPackage (pkgName: string) =
    pkgName.StartsWith("System.")
        || pkgName.StartsWith("Microsoft.")
        || pkgName.StartsWith("runtime.")
        || pkgName = "NETStandard.Library"
        || pkgName = "FSharp.Core"
        || pkgName = "Fable.Core"

type CrackedFsproj =
    { ProjectFile: string
      SourceFiles: string list
      ProjectReferences: string list
      DllReferences: IDictionary<string, string>
      PackageReferences: FablePackage list
      OtherCompilerOptions: string list }

let makeProjectOptions project sources otherOptions: FSharpProjectOptions =
    { ProjectId = None
      ProjectFileName = project
      SourceFiles = [||]
      OtherOptions = Array.distinct sources |> Array.append otherOptions
      ReferencedProjects = [| |]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.MaxValue
      UnresolvedReferences = None
      OriginalLoadReferences = []
      ExtraProjectInfo = None
      Stamp = None }

let tryGetFablePackage (opts: CrackerOptions) (dllPath: string) =
    let tryFileWithPattern dir pattern =
        try
            let files = IO.Directory.GetFiles(dir, pattern)
            match files.Length with
            | 0 -> None
            | 1 -> Some files.[0]
            | _ -> Log.always("More than one file found in " + dir + " with pattern " + pattern)
                   None
        with _ -> None
    let firstWithName localName (els: XElement seq) =
        els |> Seq.find (fun x -> x.Name.LocalName = localName)
    let tryFirstWithName localName (els: XElement seq) =
        els |> Seq.tryFind (fun x -> x.Name.LocalName = localName)
    let elements (el: XElement) =
        el.Elements()
    let attr name (el: XElement) =
        el.Attribute(XName.Get name).Value
    let child localName (el: XElement) =
        let child = el.Elements() |> firstWithName localName
        child.Value
    let firstGroupOrAllDependencies (dependencies: XElement seq) =
        match tryFirstWithName "group" dependencies with
        | Some firstGroup -> elements firstGroup
        | None -> dependencies
    if Path.GetFileNameWithoutExtension(dllPath) |> isSystemPackage
    then None
    else
        let rootDir = IO.Path.Combine(IO.Path.GetDirectoryName(dllPath), "..", "..")
        let fableDir = IO.Path.Combine(rootDir, "fable")
        match tryFileWithPattern rootDir "*.nuspec",
              tryFileWithPattern fableDir "*.fsproj" with
        | Some nuspecPath, Some fsprojPath ->
            let xmlDoc = XDocument.Load(nuspecPath)
            let metadata =
                xmlDoc.Root.Elements()
                |> firstWithName "metadata"
            let pkgId = metadata |> child "id"
            let fsprojPath =
                match Map.tryFind pkgId opts.Replace with
                | Some fsprojPath -> fsprojPath
                | None -> fsprojPath
            { Id = pkgId
              Version = metadata |> child "version"
              FsprojPath = fsprojPath
              DllPath = dllPath
              SourcePaths = []
              Dependencies =
                metadata.Elements()
                |> firstWithName "dependencies" |> elements
                // We don't consider different frameworks
                |> firstGroupOrAllDependencies
                |> Seq.map (attr "id")
                |> Seq.filter (isSystemPackage >> not)
                |> Set
            }: FablePackage |> Some
        | _ -> None

let sortFablePackages (pkgs: FablePackage list) =
    ([], pkgs) ||> List.fold (fun acc pkg ->
        match List.tryFindIndexBack (fun (x: FablePackage) -> pkg.Dependencies.Contains(x.Id)) acc with
        | None -> pkg::acc
        | Some targetIdx ->
            let rec insertAfter x targetIdx i before after =
                match after with
                | justBefore::after ->
                    if i = targetIdx then
                        if i > 0 then
                            let dependent, nonDependent =
                                List.rev before |> List.partition (fun (x: FablePackage) ->
                                    x.Dependencies.Contains(pkg.Id))
                            nonDependent @ justBefore::x::dependent @ after
                        else
                            (justBefore::before |> List.rev) @ x::after
                    else
                        insertAfter x targetIdx (i + 1) (justBefore::before) after
                | [] -> failwith "Unexpected empty list in insertAfter"
            insertAfter pkg targetIdx 0 [] acc
    )

let private getDllName (dllFullPath: string) =
    let i = dllFullPath.LastIndexOf('/')
    dllFullPath.[(i + 1) .. (dllFullPath.Length - 5)] // -5 removes the .dll extension

let getProjectOptionsFromScript (opts: CrackerOptions): CrackedFsproj list * CrackedFsproj =
    let define = opts.Define
    let scriptFile = opts.ProjFile
    let otherFlags = [|
        yield "--target:library"
#if !NETFX
        yield "--targetprofile:netcore"
#endif
        for constant in define do yield "--define:" + constant
    |]
    let checker = FSharpChecker.Create()
    checker.GetProjectOptionsFromScript(scriptFile,
                                        File.readAllTextNonBlocking scriptFile |> FSharp.Compiler.Text.SourceText.ofString,
                                        assumeDotNetFramework=false, otherFlags=otherFlags)
    |> Async.RunSynchronously
    |> fun (scriptOpts, _errors) -> // TODO: Check errors
        let dllRefs =
            scriptOpts.OtherOptions
            |> Array.filter (fun r -> r.StartsWith("-r:"))
            |> Array.map (fun r ->
                let dllRef = r.[3..]
                getDllName dllRef, dllRef)
            |> dict

        let fablePkgs =
            scriptOpts.OtherOptions
            |> List.ofArray
            |> List.map (fun line ->
                if line.StartsWith("-r:") then
                    let dllPath = line.Substring(3)
                    tryGetFablePackage opts dllPath
                else
                    None
            )
            |> List.choose id
            |> sortFablePackages

        [], { ProjectFile = scriptOpts.ProjectFileName
              SourceFiles = scriptOpts.SourceFiles |> Array.mapToList Path.normalizeFullPath
              ProjectReferences = []
              DllReferences = dllRefs
              PackageReferences = fablePkgs
              OtherCompilerOptions = [] }

let getBasicCompilerArgs (define: string[]) =
    [|
        // yield "--debug"
        // yield "--debug:portable"
        yield "--noframework"
        yield "--nologo"
        yield "--simpleresolution"
        yield "--nocopyfsharpcore"
        // yield "--define:DEBUG"
        for constant in define do
            yield "--define:" + constant
        yield "--optimize-"
        // yield "--nowarn:NU1603,NU1604,NU1605,NU1608"
        // yield "--warnaserror:76"
        yield "--warn:3"
        yield "--fullpaths"
        yield "--flaterrors"
        yield "--target:library"
#if !NETFX
        yield "--targetprofile:netstandard"
#endif
    |]

/// Simplistic XML-parsing of .fsproj to get source files, as we cannot
/// run `dotnet restore` on .fsproj files embedded in Nuget packages.
let getSourcesFromFsproj (projFile: string) =
    let withName s (xs: XElement seq) =
        xs |> Seq.filter (fun x -> x.Name.LocalName = s)
    let xmlDoc = XDocument.Load(projFile)
    let projDir = Path.GetDirectoryName(projFile)
    xmlDoc.Root.Elements()
    |> withName "ItemGroup"
    |> Seq.map (fun item ->
        (item.Elements(), [])
        ||> Seq.foldBack (fun el src ->
            if el.Name.LocalName = "Compile" then
                el.Elements() |> withName "Link"
                |> Seq.tryHead |> function
                | Some link when Path.isRelativePath link.Value ->
                    link.Value::src
                | _ ->
                    match el.Attribute(XName.Get "Include") with
                    | null -> src
                    | att -> att.Value::src
            else src))
    |> List.concat
    |> List.collect (fun fileName ->
        Path.Combine(projDir, fileName)
        |> Path.normalizeFullPath
        |> function
        | path when (path.Contains("*") || path.Contains("?")) ->
            match !! path |> List.ofSeq with
            | [] -> [ path ]
            | globResults -> globResults
        | path -> [ path ])

let private isUsefulOption (opt : string) =
    match opt with
    // We manage DEBUG (and maybe later TRACE) through CLI
    | "--define:DEBUG"
    | "--define:TRACE" -> false
    | _ ->
        [ "--define"
          "--nowarn"
          "--warnon"
          "--warnaserror"
          "--langversion" ]
        |> List.exists opt.StartsWith

let excludeProjRef (opts: CrackerOptions) (dllRefs: IDictionary<string,string>) (projRef: string) =
    let projName = Path.GetFileNameWithoutExtension(projRef)
    match opts.Exclude with
    | Some e when projRef.Contains(e) ->
        try
            opts.BuildDll(dllRefs.[projName])
        with e ->
            Log.always("Couldn't build " + projName + ": " + e.Message)
        None
    | _ ->
        let removed = dllRefs.Remove(projName)
        // if not removed then
        //     Log.always("Couldn't remove project reference " + projName + " from dll references")
        Path.normalizeFullPath projRef |> Some

/// Use Dotnet.ProjInfo (through ProjectCoreCracker) to invoke MSBuild
/// and get F# compiler args from an .fsproj file. As we'll merge this
/// later with other projects we'll only take the sources and the references,
/// checking if some .dlls correspond to Fable libraries
let fullCrack (opts: CrackerOptions): CrackedFsproj =
    let projFile = opts.ProjFile
    // Use case insensitive keys, as package names in .paket.resolved
    // may have a different case, see #1227
    let dllRefs = Dictionary(StringComparer.OrdinalIgnoreCase)

    // Try restoring project
    let projDir = IO.Path.GetDirectoryName projFile
    let projName = IO.Path.GetFileName projFile
    Process.runSync projDir "dotnet" ["restore"; projName]
    |> ignore

    Log.always("Parsing " + File.getRelativePathFromCwd projFile + "...")
    let projOpts, projRefs, _msbuildProps =
        ProjectCoreCracker.GetProjectOptionsFromProjectFile projFile

    // let targetFramework =
    //     match Map.tryFind "TargetFramework" msbuildProps with
    //     | Some targetFramework -> targetFramework
    //     | None -> failwithf "Cannot find TargetFramework for project %s" projFile

    let sourceFiles, otherOpts =
        (projOpts.OtherOptions, ([], []))
        ||> Array.foldBack (fun line (src, otherOpts) ->
            if line.StartsWith("-r:") then
                let line = Path.normalizePath (line.[3..])
                let dllName = getDllName line
                dllRefs.Add(dllName, line)
                src, otherOpts
            elif isUsefulOption line then
                src, line::otherOpts
            elif line.StartsWith("-") then
                src, otherOpts
            else
                (Path.normalizeFullPath line)::src, otherOpts)

    let fablePkgs =
        let dllRefs' = dllRefs |> Seq.map (fun (KeyValue(k,v)) -> k,v) |> Seq.toArray
        dllRefs' |> Seq.choose (fun (dllName, dllPath) ->
            match tryGetFablePackage opts dllPath with
            | Some pkg ->
                dllRefs.Remove(dllName) |> ignore
                Some pkg
            | None -> None)
        |> Seq.toList
        |> sortFablePackages

    { ProjectFile = projFile
      SourceFiles = sourceFiles
      ProjectReferences = List.choose (excludeProjRef opts dllRefs) projRefs
      DllReferences = dllRefs
      PackageReferences = fablePkgs
      OtherCompilerOptions = otherOpts }

/// For project references of main project, ignore dll and package references
let easyCrack opts dllRefs (projFile: string): CrackedFsproj =
    let projOpts, projRefs, _msbuildProps =
        ProjectCoreCracker.GetProjectOptionsFromProjectFile projFile

    let sourceFiles, otherOpts =
        (projOpts.OtherOptions, ([], []))
        ||> Array.foldBack (fun line (src, otherOpts) ->
            if isUsefulOption line then
                src, line::otherOpts
            elif line.StartsWith("-") then
                src, otherOpts
            else
                (Path.normalizeFullPath line)::src, otherOpts)

    { ProjectFile = projFile
      SourceFiles = sourceFiles
      ProjectReferences = List.choose (excludeProjRef opts dllRefs) projRefs
      DllReferences = Dictionary()
      PackageReferences = []
      OtherCompilerOptions = otherOpts }

let getCrackedProjectsFromMainFsproj (opts: CrackerOptions) =
    let mainProj = fullCrack opts
    let rec crackProjects (acc: CrackedFsproj list) (projFile: string) =
        let crackedFsproj =
            match acc |> List.tryFind (fun x -> x.ProjectFile = projFile) with
            | None -> easyCrack opts mainProj.DllReferences projFile
            | Some crackedFsproj -> crackedFsproj
        // Add always a reference to the front to preserve compilation order
        // Duplicated items will be removed later
        List.fold crackProjects (crackedFsproj::acc) crackedFsproj.ProjectReferences
    let refProjs =
        List.fold crackProjects [] mainProj.ProjectReferences
        |> List.distinctBy (fun x -> x.ProjectFile)
    refProjs, mainProj

let getCrackedProjects (opts: CrackerOptions) =
    match (Path.GetExtension opts.ProjFile).ToLower() with
    | ".fsx" ->
        getProjectOptionsFromScript opts
    | ".fsproj" ->
        getCrackedProjectsFromMainFsproj opts
    | s -> failwithf "Unsupported project type: %s" s

// It is common for editors with rich editing or 'intellisense' to also be watching the project
// file for changes. In some cases that editor will lock the file which can cause fable to
// get a read error. If that happens the lock is usually brief so we can reasonably wait
// for it to be released.
let retryGetCrackedProjects opts =
    let retryUntil = (DateTime.Now + TimeSpan.FromSeconds 2.)
    let rec retry () =
        try
            getCrackedProjects opts
        with
        | :? IO.IOException as ioex ->
            if retryUntil > DateTime.Now then
                System.Threading.Thread.Sleep 500
                retry()
            else
                failwithf "IO Error trying read project options: %s " ioex.Message
        | _ -> reraise()
    retry()

/// FAKE and other tools clean dirs but don't remove them, so check whether it doesn't exist or it's empty
let isDirectoryEmpty dir =
    not(IO.Directory.Exists(dir)) || IO.Directory.EnumerateFileSystemEntries(dir) |> Seq.isEmpty

let createFableDir (opts: CrackerOptions) =
    let fableDir = IO.Path.Combine(IO.Path.GetDirectoryName(opts.ProjFile), Naming.fableHiddenDir)
    let compilerInfo = IO.Path.Combine(fableDir, "compiler_info.txt")

    let isEmptyOrOutdated =
        if opts.ForcePkgs || isDirectoryEmpty fableDir then true
        else
            let isOutdated =
                try
                    let version = IO.File.ReadAllText(compilerInfo)
                    version <> Literals.VERSION
                with _ -> true
            if isOutdated then
                IO.Directory.Delete(fableDir, true)
            isOutdated

    if isEmptyOrOutdated then
        IO.Directory.CreateDirectory(fableDir) |> ignore
        IO.File.WriteAllText(compilerInfo, Literals.VERSION)
        IO.File.WriteAllText(IO.Path.Combine(fableDir, ".gitignore"), "**/*")

    isEmptyOrOutdated, fableDir

let copyDirIfDoesNotExist (source: string) (target: string) =
    if isDirectoryEmpty target then
        IO.Directory.CreateDirectory(target) |> ignore
        if IO.Directory.Exists source |> not then
            failwith ("Source directory is missing: " + source)
        let source = source.TrimEnd('/', '\\')
        let target = target.TrimEnd('/', '\\')
        for dirPath in IO.Directory.GetDirectories(source, "*", IO.SearchOption.AllDirectories) do
            IO.Directory.CreateDirectory(dirPath.Replace(source, target)) |> ignore
        for newPath in IO.Directory.GetFiles(source, "*.*", IO.SearchOption.AllDirectories) do
            IO.File.Copy(newPath, newPath.Replace(source, target), true)

let copyFableLibraryAndPackageSources (opts: CrackerOptions) (pkgs: FablePackage list) =
    let fableLibReset, fableLibDir = createFableDir opts

    let fableLibraryPath =
        match opts.FableLib with
        | Some path -> Path.normalizeFullPath path
        | None ->
            let assemblyDir =
              typeof<TypeInThisAssembly>.Assembly.Location
              |> Path.GetDirectoryName

            let defaultFableLibraryPaths =
                [ "../../../fable-library/"               // running from nuget tools package
                  "../../../../../build/fable-library/" ] // running from bin/Release/netcoreapp3.1
                |> List.map (fun x -> Path.GetFullPath(Path.Combine(assemblyDir, x)))

            let fableLibrarySource =
                defaultFableLibraryPaths
                |> List.tryFind IO.Directory.Exists
                |> Option.defaultValue (List.last defaultFableLibraryPaths)

            if isDirectoryEmpty fableLibrarySource then
                failwithf "fable-library directory is empty, please build FableLibrary: %s" fableLibrarySource

            Log.verbose(lazy ("fable-library: " + fableLibrarySource))
            let fableLibraryTarget = IO.Path.Combine(fableLibDir, "fable-library" + "." + Literals.VERSION)
            copyDirIfDoesNotExist fableLibrarySource fableLibraryTarget
            fableLibraryTarget

    let pkgRefs =
        pkgs |> List.map (fun pkg ->
            let sourceDir = IO.Path.GetDirectoryName(pkg.FsprojPath)
            let targetDir = IO.Path.Combine(fableLibDir, pkg.Id + "." + pkg.Version)
            copyDirIfDoesNotExist sourceDir targetDir
            { pkg with FsprojPath = IO.Path.Combine(targetDir, IO.Path.GetFileName(pkg.FsprojPath)) })

    fableLibReset, fableLibraryPath, pkgRefs

// See #1455: F# compiler generates *.AssemblyInfo.fs in obj folder, but we don't need it
let removeFilesInObjFolder sourceFiles =
    let reg = System.Text.RegularExpressions.Regex(@"[\\\/]obj[\\\/]")
    sourceFiles |> Array.filter (reg.IsMatch >> not)

let getFullProjectOpts (opts: CrackerOptions) =
    if not(IO.File.Exists(opts.ProjFile)) then
        failwith ("File does not exist: " + opts.ProjFile)

    let projRefs, mainProj = retryGetCrackedProjects opts

    let fableLibReset, fableLibDir, pkgRefs =
        copyFableLibraryAndPackageSources opts mainProj.PackageReferences

    let pkgRefs =
        pkgRefs |> List.map (fun pkg ->
            { pkg with SourcePaths = getSourcesFromFsproj pkg.FsprojPath })

    let projOpts =
        let sourceFiles =
            let pkgSources = pkgRefs |> List.collect (fun x -> x.SourcePaths)
            let refSources = projRefs |> List.collect (fun x -> x.SourceFiles)
            pkgSources @ refSources @ mainProj.SourceFiles |> List.toArray |> removeFilesInObjFolder

        let refOptions =
            projRefs
            |> List.collect (fun x -> x.OtherCompilerOptions)
            |> List.toArray

        let otherOptions =
            let coreRefs = HashSet Standalone.Metadata.references_core
            let ignoredRefs = HashSet [
               "WindowsBase"
               "Microsoft.Win32.Primitives"
               "Microsoft.VisualBasic"
               "Microsoft.VisualBasic.Core"
               "Microsoft.CSharp"
            ]
            [|
                yield! refOptions // merged options from all referenced projects
                yield! mainProj.OtherCompilerOptions // main project compiler options
                yield! getBasicCompilerArgs opts.Define // options from compiler args
                yield "--optimize" + (if opts.Optimize then "+" else "-")
                // We only keep dllRefs for the main project
                yield! mainProj.DllReferences.Values
                        // Remove unneeded System dll references
                        |> Seq.choose (fun r ->
                            let name = getDllName r
                            if ignoredRefs.Contains(name) ||
                               (name.StartsWith("System.") && not(coreRefs.Contains(name))) then None
                            else Some("-r:" + r))
            |]

        makeProjectOptions opts.ProjFile sourceFiles otherOptions

    { ProjectOptions = projOpts
      Packages = pkgRefs
      FableLibReset = fableLibReset
      FableLibDir = fableLibDir }
