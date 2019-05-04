module internal CWTools.Rules.RulesHelpers
open System
open System.IO
open CWTools.Games
open CWTools.Process
open FSharp.Collections.ParallelSeq
open CWTools.Utilities.Utils



let getTypesFromDefinitions (ruleapplicator : RuleValidationService<_>) (types : TypeDefinition<_> list) (es : Entity list) =
    let entities = es |> List.map (fun e -> ((Path.GetDirectoryName e.logicalpath).Replace("\\","/")), e, (Path.GetFileName e.logicalpath), e.validate)
    let getTypeInfo (def : TypeDefinition<_>) =
        entities |> List.choose (fun (path, e, file, validate) -> if FieldValidators.checkPathDir def path file then Some (e.entity, file, validate) else None)
                 |> List.collect (fun (e, f, v) ->
                        let inner (n : Node) =
                            let subtypes = ruleapplicator.TestSubType(def.subtypes, n) |> snd |> List.map (fun s -> def.name + "." + s)
                            let key =
                                match def.nameField with
                                |Some f -> n.TagText f
                                |None -> n.Key
                            let result = def.name::subtypes |> List.map (fun s -> s, (v, key, n.Position))
                            if CWTools.Rules.FieldValidators.typekeyfilter def n.Key then result else []
                        let childres =
                            let rec skiprootkey (srk : SkipRootKey list) (n : Node)=
                                match srk with
                                |[] -> []
                                |[SpecificKey key] ->
                                    //Too may levels deep
                                    if n.Key == key then n.Children |> List.collect inner else []
                                |[AnyKey] ->
                                    n.Children |> List.collect inner
                                |(SpecificKey key)::tail ->
                                    if n.Key == key then n.Children |> List.collect (skiprootkey tail) else []
                                    // n.Children |> List.filter (fun c -> c.Key == key) |> List.collect (fun c -> c.Children |> List.collect (skiprootkey tail))
                                |(AnyKey)::tail ->
                                    n.Children |> List.collect (skiprootkey tail)
                                    // n.Children |> List.collect (fun c -> c.Children |> List.collect (skiprootkey tail))
                            match def.type_per_file, def.skipRootKey with
                            |true, _ ->
                                let subtypes = ruleapplicator.TestSubType(def.subtypes, e) |> snd |> List.map (fun s -> def.name + "." + s)
                                def.name::subtypes |> List.map (fun s -> s, (v, Path.GetFileNameWithoutExtension f, e.Position))
                            |false, [] ->
                                (e.Children |> List.collect inner)
                            |false, srk ->
                                e.Children |> List.collect (skiprootkey srk)
                            // |false, _ ->
                        childres
                        @
                        (e.LeafValues |> List.ofSeq |> List.map (fun lv -> def.name, (v ,lv.Value.ToString(), lv.Position))))
    let results = types |> Seq.ofList |> PSeq.collect getTypeInfo |> List.ofSeq |> List.fold (fun m (n, k) -> if Map.containsKey n m then Map.add n (k::m.[n]) m else Map.add n [k] m) Map.empty
    types |> List.map (fun t -> t.name) |> List.fold (fun m k -> if Map.containsKey k m then m else Map.add k [] m ) results

let getEnumsFromComplexEnums (complexenums : (ComplexEnumDef) list) (es : Entity list) =
    let entities = es |> List.map (fun e -> e.logicalpath.Replace("\\","/"), e)
    let rec inner (enumtree : Node) (node : Node) =
        // log "%A %A" (enumtree.ToRaw) (node.Position.FileName)
        match enumtree.Children with
        |head::_ ->
            if enumtree.Children |> List.exists (fun n -> n.Key = "enum_name")
            then node.Children |> List.map (fun n -> n.Key.Trim([|'\"'|])) else
            node.Children |> List.collect (inner head)
         // TODO: Also check Leaves/leafvalues here when both are defined
        |[] ->
            if enumtree.LeafValues |> Seq.exists (fun lv -> lv.ValueText = "enum_name")
            then node.LeafValues |> Seq.map (fun lv -> lv.ValueText.Trim([|'\"'|])) |> List.ofSeq
            else
                match enumtree.Leaves |> Seq.tryFind (fun l -> l.ValueText = "enum_name") with
                |Some leaf -> node.TagsText (leaf.Key) |> Seq.map (fun k -> k.Trim([|'\"'|])) |> List.ofSeq
                |None ->
                    match enumtree.Leaves |> Seq.tryFind (fun l -> l.Key == "enum_name") with
                    |Some leaf -> node.Leaves |> Seq.map(fun l -> l.Key.Trim([|'\"'|])) |> List.ofSeq
                    |None -> []
    let getEnumInfo (complexenum : ComplexEnumDef) =
        let cpath = complexenum.path.Replace("\\","/")
        // log "cpath %A %A" cpath (entities |> List.map (fun (_, e) -> e.logicalpath))
        let values = entities |> List.choose (fun (path, e) -> if path.StartsWith(cpath, StringComparison.OrdinalIgnoreCase) then Some e.entity else None)
                              |> List.collect (fun e -> if complexenum.start_from_root then inner complexenum.nameTree e else  e.Children |> List.collect (inner complexenum.nameTree))
        // log "%A %A" complexenum.name values
        { key = complexenum.name; values = values; description = complexenum.description }
    complexenums |> List.toSeq |> PSeq.map getEnumInfo |> List.ofSeq

let getDefinedVariables (infoService : InfoService<_>) (es : Entity list) =
    // let results = es |> List.toSeq |> PSeq.fold (fun c e -> infoService.GetDefinedVariables(c,e)) (Collections.Map.empty)//|> List.ofSeq |> List.fold (fun m (n, k) -> if Map.containsKey n m then Map.add n (k::m.[n]) m else Map.add n [k] m) Collections.Map.empty
    let results = es |> List.toSeq |> PSeq.map (fun e -> infoService.GetDefinedVariables(e))
                        |> Seq.fold (fun m map -> Map.toList map |>  List.fold (fun m2 (n,k) -> if Map.containsKey n m2 then Map.add n (k@m2.[n]) m2 else Map.add n k m2) m) Collections.Map.empty
    results