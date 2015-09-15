﻿namespace MBrace.Runtime

open System
open System.Threading
open System.Threading.Tasks
open System.Runtime.Serialization
open System.Collections.Generic
open System.Collections.Concurrent

open Nessos.FsPickler
open Nessos.Vagabond

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.PrettyPrinters

/// Represents a cloud computation that is being executed in the cluster.
[<AbstractClass>]
type CloudTask internal () =

    /// Gets the parent cancellation token for the cloud task
    abstract CancellationToken : ICloudCancellationToken

    /// <summary>
    ///     Asynchronously awaits boxed result of given cloud process.
    /// </summary>
    /// <param name="timeoutMilliseconds">Timeout in milliseconds. Defaults to infinite timeout.</param>
    abstract AwaitResultBoxed : ?timeoutMilliseconds:int -> Async<obj>
    /// <summary>
    ///     Return the result if available or None if not available.
    /// </summary>
    abstract TryGetResultBoxed : unit -> Async<obj option>

    /// Awaits the boxed result of the process.
    abstract ResultBoxed : obj

    /// Date of process execution start.
    abstract StartTime : DateTime option

    /// TimeSpan of executing process.
    abstract ExecutionTime : TimeSpan option

    /// DateTime of task completion
    abstract CompletionTime : DateTime option

    /// Active number of work items related to the process.
    abstract ActiveWorkItems : int
    /// Max number of concurrently executing work items for process.
    abstract MaxActiveWorkItems : int
    /// Number of work items that have been completed for process.
    abstract CompletedWorkItems : int
    /// Number of faults encountered while executing work items for process.
    abstract FaultedWorkItems : int
    /// Total number of work items related to the process.
    abstract TotalWorkItems : int
    /// Process execution status.
    abstract Status : CloudTaskStatus

    /// Task identifier
    abstract Id : string
    /// Task user-supplied name
    abstract Name : string option
    /// Process return type
    abstract Type : Type

    /// Cancels execution of given process
    abstract Cancel : unit -> unit

    /// Task cloud logs observable
    [<CLIEvent>]
    abstract Logs : IEvent<CloudLogEntry>

    /// Asynchronously fetches log all log entries generated by given task.
    abstract GetLogsAsync : unit -> Async<CloudLogEntry []>

    /// Fetches log all log entries generated by given task.
    abstract GetLogs : unit -> CloudLogEntry []

    /// Displays all logs generated by task to stdout
    abstract ShowLogs : unit -> unit


    interface ICloudTask with
        member x.Id: string = x.Id

        member x.AwaitResultBoxed(?timeoutMilliseconds: int): Async<obj> = 
            x.AwaitResultBoxed(?timeoutMilliseconds = timeoutMilliseconds)
    
        member x.CancellationToken = x.CancellationToken
        member x.IsCanceled: bool = 
            match x.Status with
            | CloudTaskStatus.Canceled -> true
            | _ -> false
        
        member x.IsCompleted: bool = 
            match x.Status with
            | CloudTaskStatus.Completed -> true
            | _ -> false
        
        member x.IsFaulted: bool = 
            match x.Status with
            | CloudTaskStatus.Faulted | CloudTaskStatus.UserException -> true
            | _ -> false

        member x.ResultBoxed: obj = x.ResultBoxed
        member x.Status: TaskStatus = x.Status.TaskStatus
        member x.TryGetResultBoxed(): Async<obj option> = x.TryGetResultBoxed()

    /// Gets a printed report on the current process status
    member p.GetInfo() : string = CloudTaskReporter.Report([|p|], "Process", false)

    /// Prints a report on the current process status to stdout
    member p.ShowInfo () : unit = Console.WriteLine(p.GetInfo())

/// Represents a cloud computation that is being executed in the cluster.
and [<Sealed; DataContract; NoEquality; NoComparison>] CloudTask<'T> internal (source : ICloudTaskCompletionSource, runtime : IRuntimeManager) =
    inherit CloudTask()

    let [<DataMember(Name = "TaskCompletionSource")>] entry = source
    let [<DataMember(Name = "RuntimeId")>] runtimeId = runtime.Id

    let mkCell () = CacheAtom.Create(async { return! entry.GetState() }, intervalMilliseconds = 500)

    let [<IgnoreDataMember>] mutable lockObj = new obj()
    let [<IgnoreDataMember>] mutable cell = mkCell()
    let [<IgnoreDataMember>] mutable runtime = runtime
    let [<IgnoreDataMember>] mutable logPoller : ILogPoller<CloudLogEntry> option = None

    let getLogEvent() =
        match logPoller with
        | Some l -> l
        | None ->
            lock lockObj (fun () ->
                match logPoller with
                | None ->
                    let l = runtime.CloudLogManager.GetCloudLogPollerByTask(source.Id) |> Async.RunSync
                    logPoller <- Some l
                    l
                | Some l -> l)

    /// Triggers elevation in event of serialization
    [<OnSerialized>]
    let _onDeserialized (_ : StreamingContext) = 
        lockObj <- new obj()
        cell <- mkCell()
        runtime <- RuntimeManagerRegistry.Resolve runtimeId

    /// <summary>
    ///     Asynchronously awaits task result
    /// </summary>
    /// <param name="timeoutMilliseconds">Timeout in milliseconds. Defaults to infinite timeout.</param>
    member __.AwaitResult (?timeoutMilliseconds:int) : Async<'T> = async {
        let timeoutMilliseconds = defaultArg timeoutMilliseconds Timeout.Infinite
        let! result = Async.WithTimeout(async { return! entry.AwaitResult() }, timeoutMilliseconds) 
        return unbox<'T> result.Value
    }

    /// <summary>
    ///     Attempts to get task result. Returns None if not completed.
    /// </summary>
    member __.TryGetResult () : Async<'T option> = async {
        let! result = entry.TryGetResult()
        return result |> Option.map (fun r -> unbox<'T> r.Value)
    }

    /// Synchronously awaits task result 
    member __.Result : 'T = __.AwaitResult() |> Async.RunSync

    override __.AwaitResultBoxed (?timeoutMilliseconds:int) = async {
        let! r = __.AwaitResult(?timeoutMilliseconds = timeoutMilliseconds)
        return box r
    }

    override __.TryGetResultBoxed () = async {
        let! r = __.TryGetResult()
        return r |> Option.map box
    }

    override __.ResultBoxed = __.Result |> box

    override __.StartTime =
        match cell.Value.ExecutionTime with
        | NotStarted -> None
        | Started(st,_) -> Some st
        | Finished(st,_,_) -> Some st

    override __.ExecutionTime =
        match cell.Value.ExecutionTime with
        | NotStarted -> None
        | Started(_,et) -> Some et
        | Finished(_,et,_) -> Some et

    override __.CompletionTime =
        match cell.Value.ExecutionTime with
        | Finished(_,_,ct) -> Some ct
        | _ -> None

    override __.CancellationToken = entry.Info.CancellationTokenSource.Token
    /// Active number of work items related to the process.
    override __.ActiveWorkItems = cell.Value.ActiveWorkItemCount
    override __.MaxActiveWorkItems = cell.Value.MaxActiveWorkItemCount
    override __.CompletedWorkItems = cell.Value.CompletedWorkItemCount
    override __.FaultedWorkItems = cell.Value.FaultedWorkItemCount
    override __.TotalWorkItems = cell.Value.TotalWorkItemCount
    override __.Status = cell.Value.Status
    override __.Id = entry.Id
    override __.Name = entry.Info.Name
    override __.Type = typeof<'T>
    override __.Cancel() = entry.Info.CancellationTokenSource.Cancel()

    [<CLIEvent>]
    override __.Logs = getLogEvent() :> IEvent<CloudLogEntry>

    override __.GetLogsAsync() = async { 
        let! entries = runtime.CloudLogManager.GetAllCloudLogsByTask __.Id
        return entries |> Seq.sortBy (fun e -> e.DateTime) |> Seq.toArray
    }

    override __.GetLogs() = 
        runtime.CloudLogManager.GetAllCloudLogsByTask __.Id
        |> Async.RunSync
        |> Seq.sortBy(fun e -> e.DateTime)
        |> Seq.toArray

    override __.ShowLogs () =
        let entries = runtime.CloudLogManager.GetAllCloudLogsByTask __.Id |> Async.RunSync
        for e in entries do Console.WriteLine(CloudLogEntry.Format(e, showDate = true))

    interface ICloudTask<'T> with
        member x.AwaitResult(timeoutMilliseconds: int option): Async<'T> =
            x.AwaitResult(?timeoutMilliseconds = timeoutMilliseconds)
        
        member x.CancellationToken: ICloudCancellationToken = 
            entry.Info.CancellationTokenSource.Token
        
        member x.Result: 'T = x.Result
        
        member x.Status: TaskStatus = cell.Value.Status.TaskStatus
        
        member x.TryGetResult(): Async<'T option> = x.TryGetResult()

/// Cloud Process client object
and [<AutoSerializable(false)>] internal CloudTaskManagerClient(runtime : IRuntimeManager) =
    static let clients = new ConcurrentDictionary<IRuntimeId, IRuntimeManager> ()
    do clients.TryAdd(runtime.Id, runtime) |> ignore

    member __.Id = runtime.Id

    /// <summary>
    ///     Fetches task by provided task id.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    member self.GetTaskBySource (entry : ICloudTaskCompletionSource) = async {
        let! assemblies = runtime.AssemblyManager.DownloadAssemblies(entry.Info.Dependencies)
        let loadInfo = runtime.AssemblyManager.LoadAssemblies(assemblies)
        for li in loadInfo do
            match li with
            | NotLoaded id -> runtime.SystemLogger.Logf LogLevel.Error "could not load assembly '%s'" id.FullName 
            | LoadFault(id, e) -> runtime.SystemLogger.Logf LogLevel.Error "error loading assembly '%s':\n%O" id.FullName e
            | Loaded _ -> ()

        let returnType = runtime.Serializer.UnPickleTyped entry.Info.ReturnType
        let ex = Existential.FromType returnType
        let task = ex.Apply { 
            new IFunc<CloudTask> with 
                member __.Invoke<'T> () = new CloudTask<'T>(entry, runtime) :> CloudTask
        }

        return task
    }

    member self.TryGetTask(id : string) = async {
        let! source = runtime.TaskManager.TryGetTask id
        match source with
        | None -> return None
        | Some e ->
            let! t = self.GetTaskBySource e
            return Some t
    }


    member self.GetAllTasks() = async {
        let! entries = runtime.TaskManager.GetAllTasks()
        return!
            entries
            |> Seq.map (fun e -> self.GetTaskBySource e)
            |> Async.Parallel
    }

    member __.ClearTask(task:CloudTask) = async {
        do! runtime.TaskManager.Clear(task.Id)
    }

    /// <summary>
    ///     Clears all processes from the runtime.
    /// </summary>
    member pm.ClearAllTasks() = async {
        do! runtime.TaskManager.ClearAllTasks()
    }

    /// Gets a printed report of all currently executing processes
    member pm.GetTaskInfo() : string =
        let procs = pm.GetAllTasks() |> Async.RunSync
        CloudTaskReporter.Report(procs, "Processes", borders = false)

    /// Prints a report of all currently executing processes to stdout.
    member pm.ShowTaskInfo() : unit =
        /// TODO : add support for filtering processes
        Console.WriteLine(pm.GetTaskInfo())

    static member TryGetById(id : IRuntimeId) = clients.TryFind id

    interface IDisposable with
        member __.Dispose() = clients.TryRemove runtime.Id |> ignore
        
         
and internal CloudTaskReporter() = 
    static let template : Field<CloudTask> list = 
        [ Field.create "Name" Left (fun p -> match p.Name with Some n -> n | None -> "")
          Field.create "Process Id" Right (fun p -> p.Id)
          Field.create "Status" Right (fun p -> sprintf "%A" p.Status)
          Field.create "Execution Time" Left (fun p -> Option.toNullable p.ExecutionTime)
          Field.create "Work items" Center (fun p -> sprintf "%3d / %3d / %3d / %3d"  p.ActiveWorkItems p.FaultedWorkItems p.CompletedWorkItems p.TotalWorkItems)
          Field.create "Result Type" Left (fun p -> Type.prettyPrintUntyped p.Type) 
          Field.create "Start Time" Left (fun p -> Option.toNullable p.StartTime)
          Field.create "Completion Time" Left (fun p -> Option.toNullable p.CompletionTime)
        ]
    
    static member Report(processes : seq<CloudTask>, title : string, borders : bool) = 
        let ps = processes 
                 |> Seq.sortBy (fun p -> p.StartTime)
                 |> Seq.toList

        sprintf "%s\nWork items : Active / Faulted / Completed / Total\n" <| Record.PrettyPrint(template, ps, title, borders)