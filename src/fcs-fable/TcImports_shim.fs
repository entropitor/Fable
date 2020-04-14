// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

// Open up the compiler as an incremental service for parsing,
// type checking and intellisense-like environment-reporting.

namespace FSharp.Compiler.SourceCodeServices

open FSharp.Compiler
open FSharp.Compiler.AbstractIL
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryReader
open FSharp.Compiler.AbstractIL.Internal
open FSharp.Compiler.AbstractIL.Internal.Library
open FSharp.Compiler.AbstractIL.Internal.Utils
open FSharp.Compiler.CompileOps
open FSharp.Compiler.CompileOptions
open FSharp.Compiler.CompilerGlobalState
open FSharp.Compiler.ErrorLogger
open FSharp.Compiler.Lib
open FSharp.Compiler.Range
open FSharp.Compiler.SyntaxTree
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.Text
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeOps
open FSharp.Compiler.TypedTreePickle

open Internal.Utilities
open Internal.Utilities.Collections

//-------------------------------------------------------------------------
// TcImports shim
//-------------------------------------------------------------------------

module TcImports =

    let internal BuildTcImports (tcConfig: TcConfig, references: string[], readAllBytes: string -> byte[]) =
        let tcImports = TcImports ()
        let ilGlobals = IL.EcmaMscorlibILGlobals

        let sigDataReaders ilModule =
            [ for resource in ilModule.Resources.AsList do
                if IsSignatureDataResource resource then 
                    let _ccuName = GetSignatureDataResourceName resource
                    yield resource.GetBytes() ]

        let optDataReaders ilModule =
            [ for resource in ilModule.Resources.AsList do
                if IsOptimizationDataResource resource then
                    let _ccuName = GetOptimizationDataResourceName resource
                    yield resource.GetBytes() ]

        let LoadMod (ccuName: string) =
            let fileName =
                if ccuName.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase)
                then ccuName
                else ccuName + ".dll"
            let bytes = readAllBytes fileName
            let opts: ILReaderOptions =
                  { metadataOnly = MetadataOnlyFlag.Yes
                    reduceMemoryUsage = ReduceMemoryFlag.Yes
                    pdbDirPath = None
                    tryGetMetadataSnapshot = (fun _ -> None) }

            let reader = ILBinaryReader.OpenILModuleReaderFromBytes fileName bytes opts
            reader.ILModuleDef //reader.ILAssemblyRefs

        let GetSignatureData (fileName:string, ilScopeRef, ilModule:ILModuleDef option, bytes: ReadOnlyByteMemory) =
            unpickleObjWithDanglingCcus fileName ilScopeRef ilModule unpickleCcuInfo bytes

        let GetOptimizationData (fileName:string, ilScopeRef, ilModule:ILModuleDef option, bytes: ReadOnlyByteMemory) =
            unpickleObjWithDanglingCcus fileName ilScopeRef ilModule Optimizer.u_CcuOptimizationInfo bytes

        let memoize_mod = new MemoizationTable<_,_> (LoadMod, keyComparer=HashIdentity.Structural)

        let LoadSigData ccuName =
            let ilModule = memoize_mod.Apply ccuName
            let ilShortAssemName = ilModule.ManifestOfAssembly.Name
            let ilScopeRef = ILScopeRef.Assembly (mkSimpleAssemblyRef ilShortAssemName)
            let fileName = ilModule.Name //TODO: try with ".sigdata" extension
            match sigDataReaders ilModule with
            | [] -> None
            | bytes::_ -> Some (GetSignatureData (fileName, ilScopeRef, Some ilModule, bytes))

        let LoadOptData ccuName =
            let ilModule = memoize_mod.Apply ccuName
            let ilShortAssemName = ilModule.ManifestOfAssembly.Name
            let ilScopeRef = ILScopeRef.Assembly (mkSimpleAssemblyRef ilShortAssemName)
            let fileName = ilModule.Name //TODO: try with ".optdata" extension
            match optDataReaders ilModule with
            | [] -> None
            | bytes::_ -> Some (GetOptimizationData (fileName, ilScopeRef, Some ilModule, bytes))

        let memoize_sig = new MemoizationTable<_,_> (LoadSigData, keyComparer=HashIdentity.Structural)
        let memoize_opt = new MemoizationTable<_,_> (LoadOptData, keyComparer=HashIdentity.Structural)

        let GetCustomAttributesOfIlModule (ilModule: ILModuleDef) = 
            (match ilModule.Manifest with Some m -> m.CustomAttrs | None -> ilModule.CustomAttrs).AsList 

        let GetAutoOpenAttributes ilg ilModule = 
            ilModule |> GetCustomAttributesOfIlModule |> List.choose (TryFindAutoOpenAttr ilg)

        let GetInternalsVisibleToAttributes ilg ilModule = 
            ilModule |> GetCustomAttributesOfIlModule |> List.choose (TryFindInternalsVisibleToAttr ilg)

        let HasAnyFSharpSignatureDataAttribute ilModule = 
            let attrs = GetCustomAttributesOfIlModule ilModule
            List.exists IsSignatureDataVersionAttr attrs

        let mkCcuInfo ilg ilScopeRef ilModule ccu : ImportedAssembly =
              { ILScopeRef = ilScopeRef
                FSharpViewOfMetadata = ccu
                AssemblyAutoOpenAttributes = GetAutoOpenAttributes ilg ilModule
                AssemblyInternalsVisibleToAttributes = GetInternalsVisibleToAttributes ilg ilModule
#if !NO_EXTENSIONTYPING
                IsProviderGenerated = false
                TypeProviders = []
#endif
                FSharpOptimizationData = notlazy None }

        let GetCcuIL m ccuName =
            let auxModuleLoader = function
                | ILScopeRef.Local -> failwith "Unsupported reference"
                | ILScopeRef.Module x -> memoize_mod.Apply x.Name
                | ILScopeRef.Assembly x -> memoize_mod.Apply x.Name
                | ILScopeRef.PrimaryAssembly -> failwith "Unsupported reference"
            let ilModule = memoize_mod.Apply ccuName
            let ilShortAssemName = ilModule.ManifestOfAssembly.Name
            let ilScopeRef = ILScopeRef.Assembly (mkSimpleAssemblyRef ilShortAssemName)
            let fileName = ilModule.Name
            let invalidateCcu = new Event<_>()
            let ccu = Import.ImportILAssembly(
                        tcImports.GetImportMap, m, auxModuleLoader, ilScopeRef,
                        tcConfig.implicitIncludeDir, Some fileName, ilModule, invalidateCcu.Publish)
            let ccuInfo = mkCcuInfo ilGlobals ilScopeRef ilModule ccu
            ccuInfo, None

        let GetCcuFS m ccuName =
            let sigdata = memoize_sig.Apply ccuName
            let ilModule = memoize_mod.Apply ccuName
            let ilShortAssemName = ilModule.ManifestOfAssembly.Name
            let ilScopeRef = ILScopeRef.Assembly (mkSimpleAssemblyRef ilShortAssemName)
            let fileName = ilModule.Name
            let GetRawTypeForwarders ilModule =
                match ilModule.Manifest with 
                | Some manifest -> manifest.ExportedTypes
                | None -> mkILExportedTypes []
#if !NO_EXTENSIONTYPING
            let invalidateCcu = new Event<_>()
#endif
            let minfo: PickledCcuInfo = sigdata.Value.RawData //TODO: handle missing sigdata
            let codeDir = minfo.compileTimeWorkingDir
            let ccuData: CcuData = 
                  { ILScopeRef = ilScopeRef
                    Stamp = newStamp()
                    FileName = Some fileName 
                    QualifiedName = Some (ilScopeRef.QualifiedName)
                    SourceCodeDirectory = codeDir
                    IsFSharp = true
                    Contents = minfo.mspec
#if !NO_EXTENSIONTYPING
                    InvalidateEvent=invalidateCcu.Publish
                    IsProviderGenerated = false
                    ImportProvidedType = (fun ty -> Import.ImportProvidedType (tcImports.GetImportMap()) m ty)
#endif
                    UsesFSharp20PlusQuotations = minfo.usesQuotations
                    MemberSignatureEquality = (fun ty1 ty2 -> typeEquivAux EraseAll (tcImports.GetTcGlobals()) ty1 ty2)
                    TryGetILModuleDef = (fun () -> Some ilModule)
                    TypeForwarders = Import.ImportILAssemblyTypeForwarders(tcImports.GetImportMap, m, GetRawTypeForwarders ilModule)
                    }

            let optdata = lazy (
                match memoize_opt.Apply ccuName with 
                | None -> None
                | Some data ->
                    let findCcuInfo name = tcImports.FindCcu (m, name)
                    Some (data.OptionalFixup findCcuInfo) )

            let ccu = CcuThunk.Create(ilShortAssemName, ccuData)
            let ccuInfo = mkCcuInfo ilGlobals ilScopeRef ilModule ccu
            let ccuOptInfo = { ccuInfo with FSharpOptimizationData = optdata }
            ccuOptInfo, sigdata

        let rec GetCcu m ccuName =
            let ilModule = memoize_mod.Apply ccuName
            if HasAnyFSharpSignatureDataAttribute ilModule then
                GetCcuFS m ccuName
            else
                GetCcuIL m ccuName

        let fixupCcuInfo refCcusUnfixed =
            let refCcus = refCcusUnfixed |> List.map fst
            let findCcuInfo name =
                refCcus
                |> List.tryFind (fun (x: ImportedAssembly) -> x.FSharpViewOfMetadata.AssemblyName = name)
                |> Option.map (fun x -> x.FSharpViewOfMetadata)
            let fixup (data: PickledDataWithReferences<_>) =
                data.OptionalFixup findCcuInfo |> ignore
            refCcusUnfixed |> List.choose snd |> List.iter fixup
            refCcus

        let m = range.Zero
        let refCcusUnfixed = List.ofArray references |> List.map (GetCcu m)
        let refCcus = fixupCcuInfo refCcusUnfixed
        let sysCcus = refCcus |> List.filter (fun x -> x.FSharpViewOfMetadata.AssemblyName <> "FSharp.Core")
        let fslibCcu = refCcus |> List.find (fun x -> x.FSharpViewOfMetadata.AssemblyName = "FSharp.Core")

        let ccuInfos = [fslibCcu] @ sysCcus
        let ccuMap = ccuInfos |> List.map (fun ccuInfo -> ccuInfo.FSharpViewOfMetadata.AssemblyName, ccuInfo) |> Map.ofList

        // search over all imported CCUs for each cached type
        let ccuHasType (ccu: CcuThunk) (nsname: string list) (tname: string) =
            let findEntity (entityOpt: Entity option) n =
                match entityOpt with
                | None -> None
                | Some entity -> entity.ModuleOrNamespaceType.AllEntitiesByCompiledAndLogicalMangledNames.TryFind n
            let entityOpt = (Some ccu.Contents, nsname) ||> List.fold findEntity
            match entityOpt with
            | Some ns ->
                match Map.tryFind tname ns.ModuleOrNamespaceType.TypesByMangledName with
                | Some _ -> true
                | None -> false
            | None -> false

        // Search for a type
        let tryFindSysTypeCcu nsname typeName =
            let search = sysCcus |> List.tryFind (fun ccuInfo -> ccuHasType ccuInfo.FSharpViewOfMetadata nsname typeName)
            match search with
            | Some x -> Some x.FSharpViewOfMetadata
            | None ->
#if DEBUG
                printfn "Cannot find type %s.%s" (String.concat "." nsname) typeName
#endif
                None

        let tcGlobals = TcGlobals (
                            tcConfig.compilingFslib, ilGlobals, fslibCcu.FSharpViewOfMetadata,
                            tcConfig.implicitIncludeDir, tcConfig.mlCompatibility,
                            tcConfig.isInteractive, tryFindSysTypeCcu, tcConfig.emitDebugInfoInQuotations,
                            tcConfig.noDebugData, tcConfig.pathMap, tcConfig.langVersion)

#if DEBUG
        // the global_g reference cell is used only for debug printing
        do global_g <- Some tcGlobals
#endif
        // do this prior to parsing, since parsing IL assembly code may refer to mscorlib
        do tcImports.SetCcuMap(ccuMap)
        do tcImports.SetTcGlobals(tcGlobals)
        tcImports, tcGlobals
