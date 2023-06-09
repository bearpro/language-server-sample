namespace LanguageServer.HierarchicalAnalysis

open System.Threading.Tasks    
open System.Collections.Generic
open ConfigurationProcessing
open HierarchicalAnalysis

type ITokenPriorityAnalyzer =
    abstract member ReadConfigurationFromFile : configurationPath: string -> Task<bool>
    abstract member SelectPreferredModifier : ICollection<string> -> string

type TokenPriorityAnalyzer() =
    let mutable threshold = 0
    let mutable preferredModifiers: string list = []

    let performHierarchicalAnalysis config =
        let weightedAlternatives = evaluateHierarchy config.configurationTree
        let preferredAlternatives =
            weightedAlternatives
            |> Seq.where ^ fun (_, ratio) -> ratio > 0.000001
            |> Seq.sortByDescending ^ fun (_, ratio) -> ratio
            |> Seq.truncate config.threshold
            |> Seq.map ^ fun (key, _) -> key
            |> List.ofSeq
        preferredAlternatives

    let findMostPreferredModifier preferredModifiers givenModifiers =
        let modifersSet = Set.ofSeq givenModifiers

        preferredModifiers
        |> Seq.where modifersSet.Contains
        |> Seq.tryHead

    interface ITokenPriorityAnalyzer with
        member this.ReadConfigurationFromFile path = task {
            let! result = parseConfig path

            match result with
            | Ok config -> this.SetConfiguration config
                           return true
            | Error (message, error) -> 
                failwithf "%s\n%A" message error
                return false 
            }

        member _.SelectPreferredModifier modifiers = 
            findMostPreferredModifier preferredModifiers modifiers
            |> Option.defaultValue null

    member _.SetConfiguration config =
            threshold <- config.threshold
            preferredModifiers <- performHierarchicalAnalysis config
