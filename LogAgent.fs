// See: http://fsharpforfunandprofit.com/posts/concurrency-actor-model/
// See: http://msdn.microsoft.com/en-us/library/ee370357.aspx

/// Agent for logging
module internal CASO.DB.Titan.RexPro.LogAgent

// Log writer implementation using F# asynchronous agents 
open System
open System.IO
 
type private Message =
    | Debug   of string
    | Info    of string
    | Warn    of string
    | Error   of string
    | Fatal   of string
    with
        static member toString logMessage =
            match logMessage with
            | Debug msg -> ("DEBUG", msg)
            | Info msg -> ("INFO", msg)
            | Warn msg -> ("WARN", msg)
            | Error msg -> ("ERROR", msg)
            | Fatal msg -> ("FATAL", msg)
            |> fun (lvl, msg) ->
                let timeStr = DateTime.Now.ToString("dd/MM/yy HH:mm:ss.fff")
                let threadId = System.Threading.Thread.CurrentThread.ManagedThreadId
                sprintf "[%s][%s][%d]: %s" lvl timeStr threadId msg
    
        override x.ToString() =
            Message.toString x
 
type private LogCommand =
    | Log of Message
    | Flush
    | Close of AsyncReplyChannel<unit>

let inline internal write (writer:StreamWriter) message =
    printfn "%s" message
    System.Diagnostics.Debug.WriteLine message
    writer.WriteLine message
 
type LogAgent(logFile:string) as x  =
    let writer = lazy(File.AppendText logFile)
 
    let agent = MailboxProcessor.Start (fun agent ->
        // Do the loop until the Stop command is received
        // Keep the number of lines written to the log
        let rec loop(count) = async {
            let! command = agent.Receive()
            match command with
            | Log message -> 
                let count = count + 1
                let message = Message.toString message
                write writer.Value message
            | Flush ->
                if writer.IsValueCreated then
                    writer.Value.Flush()
            | Close reply ->
                let message = sprintf "%d messages written into log" count
                write writer.Value message
                x.DoClose()
                reply.Reply(ignore())
        
            return! loop(count)
        }
 
        loop(0))
 
    interface IDisposable with
        member x.Dispose() = x.DoClose()
    
    member private x.DoClose() = 
        let message = sprintf "Discarding %d messages in the queue" (agent.CurrentQueueLength)
        write writer.Value message
 
        let d = agent :> IDisposable
        d.Dispose()
 
        if writer.IsValueCreated then
            writer.Value.Dispose()
    
    member private x.log objToMessage obj = 
        obj |> objToMessage |> LogCommand.Log |> agent.Post 
 
    member x.fatal = x.log Fatal
    member x.error = x.log Error
    member x.warn  = x.log Warn
    member x.info  = x.log Info
    member x.debug = x.log Debug
 
    member x.queueLength = agent.CurrentQueueLength
    member x.flush() = LogCommand.Flush |> agent.Post
    member x.close() = LogCommand.Close |> agent.PostAndReply
