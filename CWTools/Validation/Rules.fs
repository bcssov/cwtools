namespace CWTools.Validation

open CWTools.Parser.ConfigParser
open CWTools.Process
open CWTools.Utilities.Utils
open CWTools.Validation.ValidationCore
open CWTools.Common.STLConstants
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Range
open CWTools.Games
open Stellaris.STLValidation
open FParsec
open CWTools.Parser.Types
open CWTools.Utilities

module rec Rules =
    let fst3 (a, _, _) = a
    let snd3 (_, b, _) = b
    let thd3 (_, _, c) = c

    let getValidValues =
        function
        |ValueType.Bool -> Some ["yes"; "no"]
        |ValueType.Enum es -> Some [es]
        |_ -> None

    let checkValidLeftClauseRule (enums : Map<string, string list>) (field : Field) (key : string) =
        match field with
        |LeftClauseField (ValueType.Int (min, max), _) ->
            match TryParser.parseInt key with
            |Some i -> min <= i && i <= max
            |None -> false
        |LeftClauseField (ValueType.Enum e, _) ->
            match enums.TryFind e with
            |Some es -> es |> List.contains key
            |None -> false
        |LeftClauseField (ValueType.Scalar, _) -> true
        |_ -> false

    let checkValidLeftTypeRule (types : Map<string, string list>) (field : Field) (key : string) =
        match field with
        |LeftTypeField (t, _) -> types.ContainsKey t
        |_ -> false

    type CompletionResponse =
    |Simple of label : string
    |Detailed of label : string * desc : string option
    |Snippet of label : string * snippet : string * desc : string option
    type RuleContext = 
        {
            subtypes : string list    
        }
    type RuleApplicator(rootRules : RootRule list, typedefs : TypeDefinition list , types : Map<string, string list>, enums : Map<string, string list>) =
        let aliases =
            rootRules |> List.choose (function |AliasRule (a, rs) -> Some (a, rs) |_ -> None)
        let typeRules =
            rootRules |> List.choose (function |TypeRule (rs) -> Some (rs) |_ -> None)
        let isValidValue (value : Value) =
            let key = value.ToString()
            function
            |ValueType.Bool -> 
                key = "yes" || key = "no"
            |ValueType.Enum e ->
                match enums.TryFind e with
                |Some es -> es |> List.contains key
                |None -> true
            |ValueType.Float (min, max)-> 
                match value with
                |Float f -> true
                |Int _ -> true
                |_ -> false
            |ValueType.Specific s -> key = s
            |_ -> true

        let checkValidValue (leaf : Leaf) (vt : ValueType) =
            let key = leaf.Value.ToString()
            if key.StartsWith "@" then OK else
                match vt with
                |ValueType.Bool ->
                    if key = "yes" || key = "no" then OK else Invalid[inv (ErrorCodes.CustomError "Expecting yes or no" Severity.Error) leaf]
                |ValueType.Enum e ->
                    match enums.TryFind e with
                    |Some es -> if es |> List.contains key then OK else Invalid[inv (ErrorCodes.CustomError (sprintf "Expecting one of %A" es) Severity.Error) leaf]
                    |None -> OK
                |ValueType.Float (min, max) ->
                    match leaf.Value with
                    |Float f -> if f <= max && f >= min then OK else Invalid[inv (ErrorCodes.CustomError (sprintf "Expecting a value between %f and %f" min max) Severity.Error) leaf]
                    |Int f -> if float f <= max && float f >= min then OK else Invalid[inv (ErrorCodes.CustomError (sprintf "Expecting a value between %f and %f" min max) Severity.Error) leaf]
                    |_ ->
                        match TryParser.parseDouble key with
                        |Some f -> if f < max && f > min then OK else Invalid[inv (ErrorCodes.CustomError (sprintf "Expecting a value between %f and %f" min max) Severity.Error) leaf]
                        |None -> Invalid[inv (ErrorCodes.CustomError "Expecting a number" Severity.Error) leaf]
                |ValueType.Int (min, max) ->
                    match leaf.Value with
                    |Int i -> if i <= max && i >= min then OK else Invalid[inv (ErrorCodes.CustomError (sprintf "Expecting a value between %i and %i" min max) Severity.Error) leaf]
                    |_ -> 
                        match TryParser.parseInt key with
                        |Some i ->  if i <= max && i >= min then OK else Invalid[inv (ErrorCodes.CustomError (sprintf "Expecting a value between %i and %i" min max) Severity.Error) leaf]
                        |None -> Invalid[inv (ErrorCodes.CustomError "Expecting a number" Severity.Error) leaf]
                |ValueType.Specific s -> if key.Trim([|'\"'|]) == s then OK else Invalid [inv (ErrorCodes.CustomError (sprintf "Expecting value %s" s) Severity.Error) leaf]
                |ValueType.Scalar -> OK
                |_ -> Invalid [inv (ErrorCodes.CustomError "Invalid value" Severity.Error) leaf]  

 

        let rec applyClauseField (enforceCardinality : bool) (ctx : RuleContext) (rules : Rule list) (node : Node) =
            let subtypedrules = 
                rules |> List.collect (fun (s,o,r) -> r |> (function |SubtypeField (key, shouldMatch, ClauseField cfs) -> (if (not shouldMatch) <> List.contains key ctx.subtypes then cfs else []) | x -> [(s, o, x)]))
            // let subtypedrules = 
            //     match ctx.subtype with
            //     |Some st -> rules |> List.collect (fun (s,o,r) -> r |> (function |SubtypeField (key, ClauseField cfs) -> (if key = st then cfs else []) |x -> [(s, o, x)]))
            //     |None -> rules |> List.choose (fun (s,o,r) -> r |> (function |SubtypeField (key, cf) -> None |x -> Some (s, o, x)))
            let expandedrules = 
                subtypedrules |> List.collect (
                    function 
                    | _,_,(AliasField a) -> (aliases |> List.filter (fun (s,_) -> s == a) |> List.map snd)
                    |x -> [x])
            let valueFun (leaf : Leaf) =
                match expandedrules |> List.filter (fst3 >> (==) leaf.Key) with
                |[] -> 
                    let leftTypeRule = 
                        expandedrules |> 
                        List.tryFind (function |(_, _, LeftTypeField (t, r)) -> checkValidLeftTypeRule types (LeftTypeField (t, r)) node.Key  |_ -> false )
                    match leftTypeRule with
                    |Some (_, _, f) -> applyLeafRule f leaf
                    |_ ->                     
                        if enforceCardinality then Invalid [inv (ErrorCodes.CustomError "Unexpected value" Severity.Error) leaf] else OK
                |rs -> rs <&??&> (fun (_, _, f) -> applyLeafRule f leaf)
                // match expandedrules |> List.tryFind (fst3 >> (==) leaf.Key) with
                // | Some (_, _, f) -> applyLeafRule f leaf
                // | None -> 
                //     let leftTypeRule = 
                //         expandedrules |> 
                //         List.tryFind (function |(_, _, LeftTypeField (t, r)) -> checkValidLeftTypeRule types (LeftTypeField (t, r)) node.Key  |_ -> false )
                //     match leftTypeRule with
                //     |Some (_, _, f) -> applyLeafRule f leaf
                //     |_ ->                     
                //         if enforceCardinality then Invalid [inv (ErrorCodes.CustomError "Unexpected value" Severity.Error) leaf] else OK
            let nodeFun (node : Node) =
                match expandedrules |> List.filter (fst3 >> (==) node.Key) with
                | [] ->
                    let leftClauseRule = 
                        expandedrules |> 
                        List.tryFind (function |(_, _, LeftClauseField (vt, _)) -> checkValidLeftClauseRule enums (LeftClauseField (vt, [])) node.Key  |_ -> false )
                    let leftTypeRule = 
                        expandedrules |> 
                        List.tryFind (function |(_, _, LeftTypeField (t, r)) -> checkValidLeftTypeRule types (LeftTypeField (t, r)) node.Key  |_ -> false )
                    match leftClauseRule, leftTypeRule with
                    |Some (_, _, f), _ -> applyNodeRule ctx f node
                    |_, Some (_, _, f) -> applyNodeRule ctx f node
                    |None, None -> if enforceCardinality then Invalid [inv (ErrorCodes.CustomError "Unexpected node" Severity.Error) node] else OK
                | rs -> rs <&??&> (fun (_, _, f) -> applyNodeRule ctx f node)
            let checkCardinality (node : Node) (rule : Rule) =
                let key, opts, field = rule
                match key, field with
                | "leafvalue", _
                | _, Field.SubtypeField _
                | _, Field.AliasField _ 
                | _, Field.LeftClauseField _ 
                | _, Field.LeftTypeField _ -> OK
                | _ ->
                    let leafcount = node.Tags key |> Seq.length
                    let childcount = node.Childs key |> Seq.length
                    let total = leafcount + childcount
                    if opts.min > total then Invalid [inv (ErrorCodes.CustomError (sprintf "Missing %s, requires %i" key opts.min) Severity.Error) node]
                    else if opts.max < total then Invalid [inv (ErrorCodes.CustomError (sprintf "Too many %s, max %i" key opts.max) Severity.Error) node]
                    else OK
            node.Leaves <&!&> valueFun
            <&&>
            (node.Children <&!&> nodeFun)
            <&&>
            (rules <&!&> checkCardinality node)

        and applyValueField (vt : ValueType) (leaf : Leaf) =
            checkValidValue leaf vt
            // match isValidValue leaf.Value vt with
            // | true -> OK
            // | false -> Invalid [inv (ErrorCodes.CustomError "Invalid value" Severity.Error) leaf]
        and applyObjectField (entityType : EntityType) (leaf : Leaf) =
            let values =
                match entityType with
                | EntityType.ShipSizes -> ["medium"; "large"]
                | EntityType.StarbaseModules -> ["trafficControl"]
                | EntityType.StarbaseBuilding -> ["crew"]
                | _ -> []
            let value = leaf.Value.ToString()
            if values |> List.exists (fun s -> s == value) then OK else Invalid [invCustom leaf]

        and applyTypeField (t : string) (leaf : Leaf) =
            match types.TryFind t with
            |Some values ->
                let value = leaf.Value.ToString()
                if values |> List.exists (fun s -> s == value) then OK else Invalid [invCustom leaf]
            |None -> OK            

        and applyLeftTypeFieldLeaf (t : string) (leaf : Leaf) =
            match types.TryFind t with
            |Some values ->
                let value = leaf.Key
                if values |> List.exists (fun s -> s == value) then OK else Invalid [invCustom leaf]
            |None -> OK
        and applyLeftTypeFieldNode (t : string) (node : Node) =
            match types.TryFind t with
            |Some values ->
                let value = node.Key
                if values |> List.exists (fun s -> s == value) then OK else Invalid [invCustom node]
            |None -> OK

        and applyLeafRule (rule : Field) (leaf : Leaf) =
            match rule with
            | Field.ValueField v -> applyValueField v leaf
            | Field.ObjectField et -> applyObjectField et leaf
            | Field.TargetField _ -> OK
            | Field.TypeField t -> applyTypeField t leaf
            | Field.LeftTypeField (t, f) -> applyLeftTypeFieldLeaf t leaf <&&> applyLeafRule f leaf
            | Field.ClauseField rs -> Invalid [invCustom leaf]
            | Field.LeftClauseField _ -> Invalid [invCustom leaf]
            | Field.AliasField _ -> OK
            | Field.SubtypeField _ -> OK

        and applyNodeRule (ctx : RuleContext) (rule : Field) (node : Node) =
            match rule with
            | Field.ValueField v -> Invalid [invCustom node]
            | Field.ObjectField et -> Invalid [invCustom node]
            | Field.TargetField _ -> Invalid [invCustom node]
            | Field.TypeField _ -> Invalid [invCustom node]
            | Field.LeftTypeField (t, f) -> applyLeftTypeFieldNode t node <&&> applyNodeRule ctx f node
            | Field.ClauseField rs -> applyClauseField true ctx rs node
            | Field.LeftClauseField (_, rs) -> applyClauseField true ctx rs node
            | Field.AliasField _ -> OK
            | Field.SubtypeField _ -> OK

        let testSubtype (subtypes : (string * Rule list) list) (node : Node) =
            let results = subtypes |> List.map (fun (s, rs) -> s, applyClauseField false {subtypes = []} (rs) node)
            results |> List.choose (fun (s, res) -> res |> function |Invalid _ -> None |OK -> Some s)

        let applyNodeRuleRoot (typedef : TypeDefinition) (rule : Field) (node : Node) =
            let subtypes = testSubtype (typedef.subtypes) node
            let context = { subtypes = subtypes }
            applyNodeRule context rule node
         
        let validate ((path, root) : string * Node) =
            let inner (node : Node) =
                //eprintfn "Looking for %s" (path)
                match typedefs |> List.tryFind (fun t -> path.Replace("/","\\").StartsWith(t.path.Replace("/","\\"))) with
                |Some typedef ->
                    let typerules = typeRules |> List.filter (fun (name, _, _) -> name == typedef.name)
                    //eprintfn "%A" typerules
                    let expandedRules = typerules |> List.collect (function | _,_,(AliasField a) -> (aliases |> List.filter (fun (s,_) -> s == a) |> List.map snd) |x -> [x])
                    //eprintfn "%A" expandedRules
                    match expandedRules |> List.tryFind (fun (n, _, _) -> n == typedef.name) with
                    |Some (_, _, f) -> applyNodeRuleRoot typedef f node
                    |None -> 
                        //eprintfn "Couldn't find rules for %s" typedef.name
                        OK
                |None ->
                    //eprintfn "Couldn't find rule for %s" node.Key 
                    OK
            (root.Children <&!&> inner)

        member __.ApplyNodeRule(rule, node) = applyNodeRule {subtypes = []} rule node
        //member __.ValidateFile(node : Node) = validate node
        member __.RuleValidate : StructureValidator = 
            fun _ es -> es.Raw |> List.map (fun struct(e, _) -> e.logicalpath, e.entity) <&!&> validate


    type CompletionService(rootRules : RootRule list, typedefs : TypeDefinition list , types : Map<string, string list>, enums : Map<string, string list>) =
        let aliases =
            rootRules |> List.choose (function |AliasRule (a, rs) -> Some (a, rs) |_ -> None)
        let typeRules =
            rootRules |> List.choose (function |TypeRule (rs) -> Some (rs) |_ -> None)
         
        let rec getRulePath (pos : pos) (stack : (string * bool) list) (node : Node) =
           match node.Children |> List.tryFind (fun c -> Range.rangeContainsPos c.Position pos) with
           | Some c -> getRulePath pos ((c.Key, false) :: stack) c
           | None -> 
                match node.Leaves |> Seq.tryFind (fun l -> Range.rangeContainsPos l.Position pos) with
                | Some l -> (l.Key, true)::stack
                | None -> stack

        and getCompletionFromPath (rules : Rule list) (stack : (string * bool) list) =
            let rec convRuleToCompletion (rule : Rule) =
                let s, o, f = rule
                let clause (inner : string) = Snippet (inner, (sprintf "%s = {\n\t$0\n}" inner), o.description)
                let keyvalue (inner : string) = Snippet (inner, (sprintf "%s = $0" inner), o.description)
                match o.leafvalue with
                |false ->
                    match f with
                    |Field.ClauseField _ -> [clause s]
                    |Field.ObjectField _ -> [keyvalue s]
                    |Field.ValueField _ -> [keyvalue s]
                    |Field.TypeField _ -> [keyvalue s]
                    |Field.AliasField a -> aliases |> List.choose (fun (al, rs) -> if a == al then Some rs else None) |> List.collect convRuleToCompletion
                    |Field.LeftClauseField ((ValueType.Enum e), _) -> enums.TryFind(e) |> Option.defaultValue [] |> List.map clause
                    |Field.LeftTypeField (t, ClauseField _) -> types.TryFind(t) |> Option.defaultValue [] |> List.map clause
                    |Field.LeftTypeField (t, _) -> types.TryFind(t) |> Option.defaultValue [] |> List.map keyvalue
                    |_ -> [Simple s]
                |true ->
                    match f with
                    |Field.TypeField t -> types.TryFind(t) |> Option.defaultValue [] |> List.map Simple
                    |Field.ValueField (Enum e) -> enums.TryFind(e) |> Option.defaultValue [] |> List.map Simple
                    |_ -> []
            let rec findRule (rules : Rule list) (stack) =
                let expandedRules = 
                    rules |> List.collect (
                        function 
                        | _,_,(AliasField a) -> (aliases |> List.filter (fun (s,_) -> s == a) |> List.map snd) 
                        | _,_,(SubtypeField (_, _, (ClauseField cf))) -> cf
                        |x -> [x])
                match stack with
                |[] -> expandedRules |> List.collect convRuleToCompletion
                |(key, isLeaf)::rest ->
                    let fieldToRules (field : Field) =
                        match field with
                        //|Field.EffectField -> findRule rootRules rest
                        |Field.ClauseField rs -> findRule rs rest
                        |Field.LeftClauseField (_, rs) -> findRule rs rest
                        |Field.ObjectField et ->
                            match et with
                            |EntityType.ShipSizes -> [Simple "large"; Simple "medium"]
                            |_ -> []
                        |Field.ValueField (Enum e) -> if isLeaf then enums.TryFind(e) |> Option.defaultValue [] |> List.map Simple else []
                        |Field.ValueField v -> if isLeaf then getValidValues v |> Option.defaultValue [] |> List.map Simple else []
                        |Field.TypeField t -> if isLeaf then types.TryFind(t) |> Option.defaultValue [] |> List.map Simple else []
                        |_ -> []

                    match expandedRules |> List.filter (fun (k,_,_) -> k == key) with
                    |[] -> 
                        let leftClauseRule = 
                            expandedRules |> 
                            List.tryFind (function |(_, _, LeftClauseField (vt, _)) -> checkValidLeftClauseRule enums (LeftClauseField (vt, [])) key  |_ -> false )
                        let leftTypeRule = 
                            expandedRules |> 
                            List.tryFind (function |(_, _, LeftTypeField (vt, f)) -> checkValidLeftTypeRule types (LeftTypeField (vt, f)) key  |_ -> false )
                        match leftClauseRule, leftTypeRule with
                        |Some (_, _, f), _ ->
                            match f with
                            |Field.LeftClauseField (_, rs) -> findRule rs rest
                            |_ -> expandedRules |> List.collect convRuleToCompletion
                        |_, Some (_, _, f) ->
                            match f with
                            |Field.LeftTypeField (_, rs) -> fieldToRules rs
                            |_ -> expandedRules |> List.collect convRuleToCompletion
                        |None, None ->                            
                            expandedRules |> List.collect convRuleToCompletion
                    |fs -> fs |> List.collect (fun (_, _, f) -> fieldToRules f)
            findRule rules stack |> List.distinct

        let complete (pos : pos) (node : Node) =
            let path = getRulePath pos [] node |> List.rev
            match typedefs |> List.tryFind (fun t -> node.Position.FileName.Replace("/","\\").StartsWith(t.path.Replace("/","\\"))) with
            |Some typedef ->
                let typerules = typeRules |> List.filter (fun (name, _, _) -> name == typedef.name)
                let fixedpath = if List.isEmpty path then path else (typedef.name, true)::(path |> List.tail)
                let completion = getCompletionFromPath typerules fixedpath
                completion 
            |None -> getCompletionFromPath typeRules path

        member __.Complete(pos : pos, node : Node) = complete pos node

    let getTypesFromDefinitions (types : TypeDefinition list) (es : Entity list) =
        let getTypeInfo (def : TypeDefinition) =
            es |> List.choose (fun e -> if  e.logicalpath.Replace("/","\\").StartsWith(def.path.Replace("/","\\")) then Some e.entity else None)
               |> List.collect (fun e ->
                            let inner (n : Node) =
                                match def.nameField with
                                |Some f -> n.TagText f
                                |None -> n.Key
                            e.Children |> List.map inner)
                              
        types |> List.map (fun t -> (t.name, getTypeInfo t)) |> Map.ofList