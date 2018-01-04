namespace CWTools.Process
open CWTools.Process.ProcessCore
open CWTools.Parser
open CWTools.Localisation
open CWTools.Process.ProcessCore
open CWTools.Process
open CWTools.Process.STLScopes

module STLProcess =
 
    


    let rec scriptedTriggerScope (effects : (string * Scope list) list) (triggers : (string * Scope list) list) (node : Node) =
        let anyBlockKeys = ["OR"; "AND"; "NOR"; "NAND"; "NOT"; "if"; "else"; "hidden_effect"]
        let triggerBlockKeys = ["limit"]
        let nodeScopes = node.Children 
                        |> List.map (
                            function
                            | x when List.contains x.Key anyBlockKeys ->
                                scriptedTriggerScope effects triggers x
                            | x when List.contains x.Key triggerBlockKeys -> 
                                scriptedTriggerScope triggers triggers x
                            | x ->
                                match sourceScope x.Key with
                                | Some v -> v
                                | None -> effects |> List.filter (fun (n, _) -> n = x.Key) |> List.map (fun (_, ss) -> ss) |> List.collect id
                                
                        )
        let valueScopes = node.Values |> List.map (fun v -> effects |> List.filter (fun (n, _) -> n = v.Key) |> List.map (fun (_, ss) -> ss) |> List.collect id)
        nodeScopes @ valueScopes |> List.fold (fun a b -> Set.intersect (Set.ofList a) (Set.ofList b) |> Set.toList) allScopes
        // let valueTriggers = node.Values |> List.choose (fun v -> if List.contains v.Key anyBlockKeys then None else Some v.Key)
        // //valueTriggers |> List.iter (fun f -> printfn "%A" f)
        // let nodeScopeChanges = node.Children |> List.choose (fun v -> sourceScope v.Key) //|> List.map (fun x -> [x])
        // let nodeSameScope = node.Children |> List.choose (fun v -> match sourceScope v.Key with |Some s -> None |None -> Some v)
        // let nodeTriggers = nodeSameScope |> List.choose (fun v -> if List.contains v.Key anyBlockKeys then None else Some v.Key)
        // let nodeLimit = nodeSameScope |> List.choose (fun v -> if List.contains v.Key triggerBlockKeys then Some v else None)
        // let valueScopes = (valueTriggers @ nodeTriggers)
        //                 |> List.map (fun v -> 
        //                     effects 
        //                     |> List.filter (fun (n, ss) -> n = v)
        //                     |> List.map (fun (n, ss) -> ss)
        //                     |> List.collect id)
        // let nodeRecTriggers = nodeSameScope |> List.choose (fun v -> if List.contains v.Key anyBlockKeys && not (List.contains v.Key triggerBlockKeys) then Some v else None)
        
        // let nodeScopes =
        //     nodeRecTriggers
        //     |> List.map (scriptedTriggerScope effects triggers)
        // let limitScopes = nodeLimit |> List.map (scriptedTriggerScope triggers triggers)
        //nodeScopes @ valueScopes @ nodeScopeChanges @ limitScopes
        //        |> List.fold (fun a b -> Set.intersect (Set.ofList a) (Set.ofList b) |> Set.toList) allScopes

    let getScriptedTriggerScope (effects : Effect list) (triggers : Effect list) (node : Node) =
        let effects2 = List.map (fun t -> t.name, t.scopes |> List.map parseScopes |> List.collect id) effects
        let triggers2 = List.map (fun t -> t.name, t.scopes |> List.map parseScopes |> List.collect id) triggers
        let scopes = scriptedTriggerScope effects2 triggers2 node |> List.map (fun s -> s.ToString().ToLower())
        {name = node.Key; desc = ""; usage = ""; scopes = scopes ; targets = []}

    type Ship (key, pos) =
        inherit Node(key, pos)
        member this.Name = this.TagText "name"
        member this.ShipSize = this.TagText "ship_size"
    
    type ShipSection (key, pos) =
        inherit Node(key, pos)
        member this.Template = this.TagText "template"
        member this.Slot = this.TagText "slot"
    
    type ShipComponent (key, pos) =
        inherit Node(key, pos)
        member this.Template = this.TagText "template"
        member this.Slot = this.TagText "slot"

    type Event(key, pos) =
        inherit Node(key, pos)
        member this.ID = this.TagText "id"
        member this.Desc = this.TagText "desc"
        member this.Hidden = this.Tag "hide_window" |> (function | Some (Bool b) -> b | _ -> false)
   
    let shipMap =
        [
            (=) "ship_design", processNode<Ship>;
            (=) "section", processNode<ShipSection>;
            (=) "component", processNode<ShipComponent>;
            (=) "planet_event", processNode<Event>;
            (=) "country_event", processNode<Event>;
            (=) "fleet_event", processNode<Event>;
            (=) "ship_event", processNode<Event>;
            (=) "pop_faction_event", processNode<Event>;
            (=) "pop_event", processNode<Event>;
       ]
    let shipProcess = BaseProcess(shipMap)

    

    