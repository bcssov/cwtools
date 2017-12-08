namespace CK2Events.ViewModels

open ElectronNET.API
open CK2Events.Application
open System.IO

type BaseViewModel (settings) =
    member val settings : CK2Settings = settings
    member val isElectronActive = HybridSupport.IsElectronActive

type SettingsViewModel (settings) = 
    inherit BaseViewModel (settings)
    let testGameDirectory dir =
            let exeExists = Directory.EnumerateFiles dir |> List.ofSeq |> List.exists (fun f -> Path.GetFileName(f) = "CK2game.exe")
            let locExists = Directory.EnumerateDirectories dir |> List.ofSeq |> List.exists (fun f -> Path.GetDirectoryName(f) = "localisation")
            let eveExists = Directory.EnumerateDirectories dir |> List.ofSeq |> List.exists (fun f -> Path.GetDirectoryName(f) = "events")
            exeExists && locExists && eveExists
    
    member val validDirectory = settings.gameDirectory = "" || testGameDirectory settings.gameDirectory