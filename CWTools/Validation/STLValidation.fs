namespace CWTools.Validation
open CWTools.Validation.ValidationCore
open CWTools.Process.STLProcess
open CWTools.Process
open CWTools.Process.ProcessCore
open CWTools.Parser
open CWTools.Process.STLScopes
open CWTools.Common
open CWTools.Common.STLConstants
open DotNet.Globbing
open CWTools.Games


module STLValidation =
    type S = Severity
    type EntitySet(entities : Entity list) =
        member __.GlobMatch(pattern : string) =
            let glob = Glob.Parse(pattern)
            entities |> List.choose (fun es -> if glob.IsMatch(es.filepath) then Some es.entity else None)
        member this.GlobMatchChildren(pattern : string) =
            this.GlobMatch(pattern) |> List.map (fun e -> e.Children) |> List.collect id
        member __.All = entities |> List.map (fun es -> es.entity)
        member __.Raw = entities
        member this.Merge(y : EntitySet) = EntitySet(this.Raw @ y.Raw)

    type StructureValidator = EntitySet -> EntitySet -> ValidationResult
    let shipName (ship : Ship) = if ship.Name = "" then Invalid [(inv S.Error ship "must have name")] else OK
    let shipSize (ship : Ship) = if ship.ShipSize = "" then Invalid [(inv S.Error ship "must have size")] else OK

    let validateShip : Validator<Ship>  = shipName <&> shipSize


    let getDefinedVariables (node : Node) =
        let fNode = (fun (x:Node) children ->
                        let values = x.Values |> List.choose (function  kv when kv.Key.StartsWith("@") -> Some kv.Key |_ -> None)
                        values @ children)
        let fCombine = (@)
        node |> (foldNode2 fNode fCombine [])

    let checkUsedVariables (node : Node) (variables : string list) =
        let fNode = (fun (x:Node) children ->
                        let values = x.Values |> List.choose (fun v -> match v.Value with |String s when s.StartsWith("@") -> Some v |_ -> None)
                        match values with
                        | [] -> children
                        | x -> 
                            x |> List.map ((fun f -> f, f.Value.ToString()) >> (fun (l, v) -> if variables |> List.contains v then OK else Invalid [inv S.Error l (v + " is not defined")]))
                              |> List.fold (<&&>) OK)
        let fCombine = (<&&>)
        node |> (foldNode2 fNode fCombine OK)
    
    

    let validateVariables : StructureValidator =
        fun _ es ->
            let x =  
                es.All  
                |> List.map
                    (fun node -> 
                        let defined = getDefinedVariables node
                        let errors = checkUsedVariables node defined
                        errors
                    )
            x |> List.fold (<&&>) OK

    let eventScope (event : Event) =
        match event.Key with
        |"country_event" -> Scope.Country
        |"fleet_event" -> Scope.Fleet
        |"ship_event" -> Scope.Ship
        |"pop_faction_event" -> Scope.PopFaction
        |"pop_event" -> Scope.Pop
        |"planet_event" -> Scope.Planet
        |_ -> Scope.Army

    let handleUnknownTrigger root (key : string) =
        match STLProcess.ignoreKeys |> List.tryFind (fun k -> k.ToLower() = key.ToLower()) with
        |Some _ -> OK //Do better
        |None -> if key.StartsWith("@") then OK else Invalid [inv S.Error root (sprintf "unknown trigger %s used." key)]
    
    let handleUnknownEffect root (key : string) =
        match STLProcess.ignoreKeys |> List.tryFind (fun k -> k.ToLower() = key.ToLower()) with
        |Some _ -> OK //Do better
        |None -> if key.StartsWith("@") then OK else Invalid [inv S.Error root (sprintf "unknown effect %s used." key)]
    

    let rec valEventTrigger (root : Node) (triggers : (Effect * bool) list) (effects : (Effect * bool) list) (scope : Scope) (effect : Both) =
        match effect with
        |LeafI leaf ->
            match triggers |> List.tryFind (fun (e, _) -> e.Name = leaf.Key ) with
            |Some (_, true) -> OK
            |Some (t, false) -> Invalid [inv S.Error root (sprintf "%s trigger used in incorrect scope. In %A but expected %s" leaf.Key scope (t.Scopes |> List.map (fun f -> f.ToString()) |> String.concat ", "))]
            |None -> handleUnknownTrigger root leaf.Key
        |NodeI node ->
            match node.Key with
            |x when STLProcess.toTriggerKeys @ STLProcess.toTriggerBlockKeys |> List.contains (x.ToLower()) ->
                valNodeTriggers root triggers effects scope node
            |x when STLProcess.isTargetKey x ->
                OK //Handle later
            |x when x.StartsWith("event_target:") ->
                OK //Handle later
            |x when x.StartsWith("parameter:") ->
                OK //Handle later
            |x ->
                match changeScope effects triggers x scope with
                |NewScope s -> valNodeTriggers root triggers effects s node
                |WrongScope ss -> Invalid [inv S.Error node (sprintf "%s scope command used in incorrect scope. In %A but expected %s" x scope (ss |> List.map (fun s -> s.ToString()) |> String.concat ", "))]
                |NotFound ->
                    match triggers |> List.tryFind (fun (e, _) -> e.Name.ToLower() = x.ToLower() ) with
                    |Some (_, true) -> OK
                    |Some (t, false) -> Invalid [inv S.Error node (sprintf "%s trigger used in incorrect scope. In %A but expected %s" x scope (t.Scopes |> List.map (fun f -> f.ToString()) |> String.concat ", "))]
                    |None -> handleUnknownTrigger node x
        |_ -> OK

    and valEventEffect (root : Node) (triggers : (Effect * bool) list) (effects : (Effect * bool) list) (scope : Scope) (effect : Both) =
        match effect with
        |LeafI leaf ->
            match effects |> List.tryFind (fun (e, _) -> e.Name = leaf.Key) with
            |Some(_, true) -> OK
            |Some (t, false) -> Invalid [inv S.Error root (sprintf "%s effect used in incorrect scope. In %A but expected %s" leaf.Key scope (t.Scopes |> List.map (fun f -> f.ToString()) |> String.concat ", "))]
            |None -> handleUnknownEffect root leaf.Key
        |NodeI node ->
            match node.Key with
            |x when STLProcess.toTriggerKeys @ STLProcess.toTriggerBlockKeys |> List.contains x ->
                valNodeTriggers root triggers effects scope node
            |x when STLProcess.isTargetKey x ->
                OK //Handle later
            |x when x.StartsWith("event_target:") ->
                OK //Handle later
            |x when x.StartsWith("parameter:") ->
                OK //Handle later
            |x ->
                match changeScope effects triggers x scope with
                |NewScope s -> valNodeEffects node triggers effects s node
                |WrongScope ss -> Invalid [inv S.Error node (sprintf "%s scope command used in incorrect scope. In %A but expected %s" x scope (ss |> List.map (fun s -> s.ToString()) |> String.concat ", "))]
                |NotFound ->
                    match effects |> List.tryFind (fun (e, _) -> e.Name.ToLower() = x.ToLower()) with
                    |Some(_, true) -> OK
                    |Some (t, false) -> Invalid [inv S.Error node (sprintf "%s effect used in incorrect scope. In %A but expected %s" x scope (t.Scopes  |> List.map (fun f -> f.ToString()) |> String.concat ", "))]
                    |None -> handleUnknownEffect node x
        |_ -> OK
    
    and valNodeTriggers (root : Node) (triggers : (Effect * bool) list) (effects : (Effect * bool) list) (scope : Scope) (node : Node) =
        let scopedTriggers = triggers |> List.map (fun (e, _) -> e, e.Scopes |> List.exists (fun s -> s = scope)) 
        let scopedEffects = effects |> List.map (fun (e, _) -> e, e.Scopes |> List.exists (fun s -> s = scope)) 
        List.map (valEventTrigger root scopedTriggers scopedEffects scope) node.All |> List.fold (<&&>) OK

    and valNodeEffects (root : Node) (triggers : (Effect * bool) list) (effects : (Effect * bool) list) (scope : Scope) (node : Node) =
        let scopedTriggers = triggers |> List.map (fun (e, _) -> e, e.Scopes |> List.exists (fun s -> s = scope)) 
        let scopedEffects = effects |> List.map (fun (e, _) -> e, e.Scopes |> List.exists (fun s -> s = scope)) 
        List.map (valEventEffect root scopedTriggers scopedEffects scope) node.All |> List.fold (<&&>) OK

    let valOption (root : Node) (triggers : (Effect * bool) list) (effects : (Effect * bool) list) (scope : Scope) (node : Node) =
        let optionExcludes = ["name"; "custom_tooltip"; "response_text"; "is_dialog_only"; "sound"; "ai_chance"; "custom_gui"; "default_hide_option"]
        let filterFunction =
            function
            | NodeI n -> optionExcludes |> List.contains (n.Key.ToLower())
            | LeafI l -> optionExcludes |> List.contains (l.Key.ToLower())
            | _ -> false
        let children = node.All |> List.filter (filterFunction >> not)
        children |> List.map (valEventEffect node triggers effects scope) |> List.fold (<&&>) OK
        
    
    let valEventTriggers  (triggers : (Effect) list) (effects : (Effect) list) (event : Event) =
        let eventScope = eventScope event
        let scopedTriggers = triggers |> List.map (fun e -> e, e.Scopes |> List.exists (fun s -> s = eventScope)) 
        let scopedEffects = effects |> List.map (fun e -> e, e.Scopes |> List.exists (fun s -> s = eventScope)) 
        match event.Child "trigger" with
        |Some n -> 
            let v = List.map (valEventTrigger event scopedTriggers scopedEffects eventScope) n.All
            v |> List.fold (<&&>) OK
        |None -> OK
    let valEventEffects (triggers : (Effect) list) (effects : (Effect) list) (event : Event) =
        let eventScope = eventScope event
        let scopedTriggers = triggers |> List.map (fun e -> e, e.Scopes |> List.exists (fun s -> s = eventScope)) 
        let scopedEffects = effects |> List.map (fun e -> e, e.Scopes |> List.exists (fun s -> s = eventScope)) 
        let imm = 
            match event.Child "immediate" with
            |Some n -> 
                let v = List.map (valEventEffect event scopedTriggers scopedEffects eventScope) n.All
                v |> List.fold (<&&>) OK
            |None -> OK
        let aft = 
            match event.Child "after" with
            |Some n -> 
                let v = List.map (valEventEffect event scopedTriggers scopedEffects eventScope) n.All
                v |> List.fold (<&&>) OK
            |None -> OK
        let opts = event.Childs "option" |> List.map (fun o -> (valOption o scopedTriggers scopedEffects eventScope o))
        let opts2 = opts |> List.fold (<&&>) OK
        imm <&&> aft <&&> opts2

    /// Make sure an event either has a mean_time_to_happen or is stopped from checking all the time
    /// Not mandatory, but performance reasons, suggested by Caligula
    /// Check "mean_time_to_happen", "is_triggered_only", "fire_only_once" and "trigger = { always = no }".
    /// Create issue if none are true
    let valEventVals (event : Event) =
        let isMTTH = event.Has "mean_time_to_happen"
        let isTrig = event.Has "is_triggered_only"
        let isOnce = event.Has "fire_only_once"
        let isAlwaysNo = 
            match event.Child "trigger" with
            | Some t -> 
                match t.Tag "always" with
                | Some (Bool b) when b = false -> true
                | _ -> false
            | None -> false
        match isMTTH || isTrig || isOnce || isAlwaysNo with
        | false -> Invalid [inv S.Information event "This event might affect performance as it runs on every tick, consider adding 'is_triggered_only', 'fire_only_once' or 'mean_time_to_happen'"]
        | true -> OK

    let valResearchLeader (area : string) (cat : string option) (node : Node) =
        let fNode = (fun (x:Node) children ->
                        let results = 
                            match x.Key with
                            | "research_leader" ->
                                match x.TagText "area" with
                                | "" -> Invalid [inv S.Error x "This research_leader is missing required \"area\""]
                                | area2 when area <> area2 -> Invalid [inv S.Information x (sprintf "This research_leader is uses area %s but the technology uses area %s" area2 area)]
                                | _ -> OK
                                /// These aren't really required
                                // <&&>
                                // match cat, x.TagText "has_trait" with
                                // | None, _ -> OK
                                // | _, "" -> Invalid [inv S.Error x "This research_leader is missing required \"has_trait\""]
                                // | Some c, t when ("leader_trait_expertise_" + c) <> t -> Invalid [inv S.Warning x "This research_leader has the wrong expertise"]
                                // | _ -> OK
                            | _ -> OK
                        results <&&> children)
        let fCombine = (<&&>)
        node |> (foldNode2 fNode fCombine OK)

    let valTechnology : StructureValidator =
        fun _ es ->
            let techs = es.GlobMatchChildren("**/common/technology/*.txt")
            let inner =
                fun (node : Node) ->
                    let area = node.TagText "area"
                    let cat = node.Child "category" |> Option.bind (fun c -> c.All |> List.tryPick (function |LeafValueI lv -> Some (lv.Value.ToString()) |_ -> None))
                    let catres = 
                        match cat with
                        | None -> Invalid [inv S.Error node "No category found for this technology"]
                        | Some _ -> OK
                    catres <&&> valResearchLeader area cat node
            techs |> List.map inner |> List.fold (<&&>) OK

    let valButtonEffects : StructureValidator =
        fun os es ->
            let effects = (os.GlobMatchChildren("**/common/button_effects/*.txt"))
                            |> List.filter (fun e -> e :? Button_Effect)
                            |> List.map (fun e -> e.Key)
            let buttons = es.GlobMatchChildren("**/interface/*.gui") @ es.GlobMatchChildren("**/interface/**/*.gui")
            let fNode = (fun (x : Node) children ->
                            let results =
                                match x.Key with
                                | "effectButtonType" -> 
                                    x.Leafs "effect" <&!&> (fun e -> if List.contains (e.Value.ToRawString()) effects then OK else Invalid [inv S.Error e (sprintf "Button effect %s not found" (e.Value.ToString()))])
                                | _ -> OK
                            results <&&> children
                                )
            let fCombine = (<&&>)
            buttons <&!&> (foldNode2 fNode fCombine OK)

    let valSprites : StructureValidator = 
        //let spriteKeys = ["spriteType"; "portraitType"; "corneredTileSpriteType"; "flagSpriteType"]
        fun os es ->
            let sprites = os.GlobMatchChildren("**/interface/*.gfx") @ os.GlobMatchChildren("**/interface/*/*.gfx")
                            |> List.filter (fun e -> e.Key = "spriteTypes")
                            |> List.collect (fun e -> e.Children)
                            |> List.collect (fun s -> s.TagsText "name")
            let gui = es.GlobMatchChildren("**/interface/*.gui") @ es.GlobMatchChildren("**/interface/*/*.gui")
            let fNode = (fun (x : Node) children ->
                            let results =
                                match x.Leafs "spriteType" with
                                | [] -> OK
                                | xs -> 
                                    xs <&!&> (fun e -> if List.contains (e.Value.ToRawString()) sprites then OK else Invalid [inv S.Error e (sprintf "Sprite type %s not found" (e.Value.ToString()))])
                            results <&&> children
                                )
            let fCombine = (<&&>)
            gui <&!&> (foldNode2 fNode fCombine OK)



