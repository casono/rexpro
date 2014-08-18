namespace CASO.DB.Titan.RexPro

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.IO
open System.Text.RegularExpressions
open MsgPack.Serialization
open Newtonsoft.Json

open CASO.DB.Titan.RexPro.Messages
open CASO.DB.Titan.RexPro.BufferPoolStream

type RexProClientException(message) =
    inherit Exception(message)

type QueryResult<'a> = 
    | QuerySuccess of 'a
    | QueryError of exn   

/// Serializer types
type SerializerType =
    | MsgPack = 0
    | Json = 1
    | Unknown = 99

[<AutoOpen>]
module internal Internals =

    open System.Reflection
    open System.Collections.Generic

    open CASO.DB.Titan.RexPro.LogAgent
    open CASO.DB.Titan.RexPro.ObjectPool

    [<Literal>]
    let ProtocolVersion = 1

    [<Literal>]
    let MaxConnectionPoolSize = 1000

    [<Literal>]
    let SocketSendBufferSize = 8192 // 8KB

    [<Literal>]
    let SocketReceiveBufferSize = 8192 // 8KB

    [<Literal>]
    let SocketIdleTimeout = 300000 // If the socket is idle for more than 5 minutes - dispose it!

    [<Literal>]
    let SocketSendTimeout = 3000 // 3 second send timeout

    [<Literal>]
    let SocketReceiveTimeout = 3000 // 3 second receive timeout

    /// Simple logger
#if DEBUG
    let log = new LogAgent(sprintf @"%s\rexpro_log.txt" AppDomain.CurrentDomain.BaseDirectory)
#endif

    /// Convert to a C# style Dictionary
    let inline bindingsToDic (bindings:list<string * _>) =
        let dic = Dictionary()
        bindings |> List.iter (fun (k, v) -> dic.Add(k, v))
        dic
    
    /// Determines if this exception is critical/fatal
    let inline isCriticalException (e:Exception) =
        match e with
        | :?RexProClientException | :?AggregateException -> false // Could be treated as critical?, but this is just an example
        | :?System.ComponentModel.Win32Exception -> true
        | _ -> true // Yes all exceptions are critical
        
    /// Valid message types int's
    type MessageType =
        | SessionRequest = 1
        | SessionResponse = 2
        | ScriptRequest = 3
        | ScriptResponse = 5
        | ErrorResponse = 0
        | Unknown = 99
    
    // Our 2 valid request messages
    type RequestMessageType =
        | SessionRequest of SessionRequestMessage
        | ScriptRequest of ScriptRequestMessage

    /// Common for all messages
    type MessageHeader = {
        ProtocolVersion: byte
        SerializerType: SerializerType
        MessageType: MessageType
        MessageSize: int
    }

    /// Either open a session or close a session
    type SessionRequestMessageOption =
        | SessionOpen
        | SessionClose of Guid
    
    module Serializers =
        module MsgPack =
            /// Serializer for session request
            let sessionRequestMessageSerializer = SerializationContext.Default.GetSerializer<SessionRequestMessage>()

            /// Serializer for session response
            let sessionResponseMessageSerializer = SerializationContext.Default.GetSerializer<SessionResponseMessage>()

            /// Serializer for script request
            let scriptRequestMessageSerializer = SerializationContext.Default.GetSerializer<ScriptRequestMessage>()

            /// Cache for script response
            let scriptResponseMessageSerializerCache = new System.Collections.Concurrent.ConcurrentDictionary<Type, IMessagePackSerializer>()

            /// Serializer(s) for script responses. Cached for each type 
            let scriptResponseMessageSerializer<'a> =
                let isCached, serializer = scriptResponseMessageSerializerCache.TryGetValue(typeof<'a>)
                if isCached then
                    serializer :?> MessagePackSerializer<'a>
                else
                    let serializer = SerializationContext.Default.GetSerializer<'a>()
                    scriptResponseMessageSerializerCache.TryAdd(typeof<'a>, serializer) |> ignore
                    serializer
    
            /// Serializer for error responses
            let errorResponseMessageSerializer = SerializationContext.Default.GetSerializer<ErrorResponseMessage>()

        module Json =

            open Newtonsoft.Json.Serialization
        
            let serializer = 
                JsonSerializerSettings()
                |> fun settings ->
                    settings.ContractResolver <- new CamelCasePropertyNamesContractResolver() :> IContractResolver
                    settings.DateFormatHandling <- Newtonsoft.Json.DateFormatHandling.MicrosoftDateFormat
                    settings.ReferenceLoopHandling <- Newtonsoft.Json.ReferenceLoopHandling.Ignore
                    settings.PreserveReferencesHandling <- Newtonsoft.Json.PreserveReferencesHandling.None
                    settings.Formatting <- Formatting.None
                    JsonSerializer.Create(settings)

    /// Converts a IP string to a IP address
    let createIPEndPoint endPointStr =
        let success, ip = Net.IPAddress.TryParse endPointStr
        match success with
        | true -> Some(ip)
        | false -> None

    /// Contains data useful for socket operations
    type MessageData() =
        
        /// Used to zero out the message header after received message.
        /// See: PooledSocket.MessageData
        let blankMessageHeader = { 
            ProtocolVersion = (byte)ProtocolVersion; 
            SerializerType = SerializerType.Unknown; 
            MessageType = MessageType.Unknown; 
            MessageSize = 0; }

        /// The parsed message size
        member val MessageHeader = blankMessageHeader with get, set
        member val TotalMessageSize = 0L with get, set
        
        member x.Reset() =
            x.MessageHeader <- blankMessageHeader
            x.TotalMessageSize <- 0L

    /// Wrapping a socket for com with Rexster. Used with ObjectPool and with a ExpireTimer.
    /// So that we can dispose of unused socket connections after a timeout.
    type PooledSocket() =
        
        /// The real socket...
        let socket = new Socket(
                        AddressFamily.InterNetwork, 
                        SocketType.Stream, 
                        ProtocolType.Tcp,
                        NoDelay = true,
                        ReceiveBufferSize = SocketReceiveBufferSize,
                        SendBufferSize = SocketSendBufferSize,
                        ExclusiveAddressUse = false,
                        ReceiveTimeout = SocketReceiveTimeout,
                        SendTimeout = SocketSendTimeout,
                        Blocking = false,
                        LingerState = LingerOption(false, 0))
        
        /// An id to use with IEquatable.Equals 
        /// Used for the ObjectPool for removal when this PooledSocket has expired
        let id = Guid.NewGuid()
        let idleTimeoutElapsed = new Event<_>()
        let idleTimeoutTimer = 
            let timer = new System.Timers.Timer((float)SocketIdleTimeout)
            timer.Elapsed.Add(fun (x) -> 
            (
                idleTimeoutElapsed.Trigger()
                timer.Stop()
            ))
            timer.Enabled <- false
            timer

        /// Hold our message data (only used when receiving messages)
        let messageData = new MessageData()

        /// For signalling the thread when done with the socket actions
        let waitHandle = new AutoResetEvent(false)

        let socketEventArgs = 
            new SocketAsyncEventArgs()
            |> fun args ->
                args.Completed.Add(fun (e:SocketAsyncEventArgs) -> 
                (   
                    // The Socket IO is completed signal the thread
                    waitHandle.Set() |> ignore
                ))
                args

        member val IdleTimeoutElapsed = idleTimeoutElapsed.Publish with get
        member val MessageData = messageData with get
        member val Id = id with get
        member x.Connected with get() = socket.Connected

        member x.Connect endPoint = 
            if not socket.Connected then
                socketEventArgs.RemoteEndPoint <- endPoint
                if socket.ConnectAsync(socketEventArgs) then
                    waitHandle.WaitOne() |> ignore
                if socketEventArgs.SocketError <> SocketError.Success then 
                    raise (RexProClientException(sprintf "Could not connect: %A" socketEventArgs.SocketError))

        member x.Disconnect() = 
            try
                socket.Shutdown(SocketShutdown.Both)
                if socket.DisconnectAsync(socketEventArgs) then
                    waitHandle.WaitOne() |> ignore
                if socketEventArgs.SocketError <> SocketError.Success then 
                    raise (RexProClientException(sprintf "Could not disconnect: %A" socketEventArgs.SocketError))
            with
            | e -> 
#if DEBUG
                log.error(e.ToString())
#else
                ()
#endif

        member x.Send buffer offset count = 
            socketEventArgs.SetBuffer(buffer, offset, count)
            if socket.SendAsync(socketEventArgs) then
                waitHandle.WaitOne() |> ignore

            if socketEventArgs.SocketError <> SocketError.Success then 
                raise (RexProClientException(sprintf "Could not send: %A" socketEventArgs.SocketError))

        member x.Receive buffer offset count = 
            socketEventArgs.SetBuffer(buffer, offset, count)
            if socket.ReceiveAsync(socketEventArgs) then
                waitHandle.WaitOne() |> ignore
            if socketEventArgs.SocketError <> SocketError.Success then 
                raise (RexProClientException(sprintf "Could not receive: %A" socketEventArgs.SocketError))
            (socketEventArgs.Offset, socketEventArgs.BytesTransferred)
 
        // Stop the timer when there's activity
        member x.StopIdleTimer(e) = 
            idleTimeoutTimer.Stop()

        member x.StartIdleTimer(e) = 
            idleTimeoutTimer.Start()
                
        interface IDisposable with
            member x.Dispose() =
                socket.Close()
                socket.Dispose()
                idleTimeoutTimer.Dispose()
                GC.SuppressFinalize(x)

        member x.Dispose() = (x :> IDisposable).Dispose()

        interface IEquatable<PooledSocket> with
            member x.Equals(obj) =
                obj.Id.Equals(x.Id)

    /// Object pools for better memory handling (sockets with args, data buffers)
    let socketReceiveBufferPool = new BufferPool(SocketReceiveBufferSize, 10, MaxConnectionPoolSize * 2)
    let socketSendBufferPool = new BufferPool(SocketSendBufferSize, 10, MaxConnectionPoolSize * 2)
    let socketConnectionPool = new ObjectPool<PooledSocket>((fun() -> new PooledSocket()), 10, MaxConnectionPoolSize)

/// Client for connecting to Rexster (RexPro binary protocol)
/// using MsgPack as serializer
type RexProClient(host:string, port:int, graphName:string, username:string, password:string) =
    
    /// The remote IP address
    let remoteEndPoint = 
        match createIPEndPoint host with
        | None -> raise (RexProClientException("Invalid IP address"))
        | Some ip -> new Net.IPEndPoint(ip, port); 
    
#if DEBUG
    // For timing the queries
    let sw = new System.Diagnostics.Stopwatch()
#endif
    
    /// See: https://github.com/tinkerpop/rexster/wiki/RexPro-Messages
    let parseResponseMessageHeaders (buffer:byte[]) = 
        {
            ProtocolVersion = buffer.[0];
            SerializerType =
                match buffer.[1] with
                | 0uy -> SerializerType.MsgPack
                | 1uy -> SerializerType.Json
                | _ -> SerializerType.Unknown;
            MessageType =
                match buffer.[6] with
                | 2uy -> MessageType.SessionResponse
                | 5uy -> MessageType.ScriptResponse
                | 0uy -> MessageType.ErrorResponse
                | _ -> MessageType.Unknown;
            MessageSize = 
                Array.sub buffer 7 4 
                |> fun arr -> 
                    if BitConverter.IsLittleEndian 
                    then Array.rev arr
                    else arr
                |> fun arr ->
                    BitConverter.ToInt32(arr, 0)
        }

    /// Reads number of bytes from socket
    let rec socketReceive (socket:PooledSocket) (stream:BufferPoolStream) =
        async {

            let! buffer = socketReceiveBufferPool.Pop()

            // http://msdn.microsoft.com/en-us/library/system.net.sockets.socket.receiveasync(v=vs.110).aspx
            let offset, count = socket.Receive buffer 0 buffer.Length

            if count > 0 then           
            
                // Append the received data. Take from the socket eventargs offset, and the bytesTransferred property
                stream.Write(buffer, offset, count)

                // Check if the message header is parsed, if not parse it
                if socket.MessageData.TotalMessageSize = 0L && count >= 11 then
                    socket.MessageData.MessageHeader <- parseResponseMessageHeaders buffer
                    socket.MessageData.TotalMessageSize <- (int64)(socket.MessageData.MessageHeader.MessageSize + 11)

                // If there's more data read into the same stream
                if socket.MessageData.TotalMessageSize > 0L && stream.Length < socket.MessageData.TotalMessageSize then
                    do! socketReceive socket stream
            
            Array.fill buffer 0 buffer.Length 0uy
            socketReceiveBufferPool.Push(buffer)
        }

    /// Writes bytes to socket
    let rec socketSend (socket:PooledSocket) (stream:BufferPoolStream) =
        async {

            let! buffer = socketSendBufferPool.Pop()

            // Get the remaining length
            let readLength = 
                (int)(stream.Length - stream.Position)
                |> fun len ->
                    if len > buffer.Length 
                    then buffer.Length
                    else len
            // We can ignore because we know it's going to fill up the buffer
            // See: BufferPoolStream
            stream.Read(buffer, 0, readLength) |> ignore

            socket.Send buffer 0 readLength

            if stream.Position < stream.Length then
                do! socketSend socket stream

            Array.fill buffer 0 buffer.Length 0uy
            socketSendBufferPool.Push(buffer)
        }
    
    /// See: https://github.com/tinkerpop/rexster/wiki/RexPro-Messages
    /// Writes the header bytes to the stream
    let writeCommonMessageHeader (serializerType:SerializerType) (stream:Stream) =
        stream.Write([|(byte)ProtocolVersion|], 0, 1)
        stream.Write([|(byte)serializerType|], 0, 1)
        stream.Write([|0uy; 0uy; 0uy; 0uy;|], 0, 4)

    /// Writes the body type and body bytes to the stream
    let insertMessageBodyDetails (msgType:MessageType) (msgSize:int) (stream:Stream) =
        stream.Write([|(byte)msgType|], 0, 1)
        if BitConverter.IsLittleEndian 
        then stream.Write(BitConverter.GetBytes(msgSize) |> Array.rev, 0, 4) // 4 bytes of our length
        else stream.Write(BitConverter.GetBytes(msgSize), 0, 4) // 4 bytes of our length
    
    /// Fill the send stream with our data/message
    let fillSendStream message (serializerType:SerializerType) sendStream =
        // Use a memorystream for writing our send data 
        sendStream |> writeCommonMessageHeader serializerType
        sendStream.Seek(11L, SeekOrigin.Begin) |> ignore

        // Write the body bytes
        let msgType =
            match message with
            | ScriptRequest msg ->
                match serializerType with
                | SerializerType.MsgPack -> Serializers.MsgPack.scriptRequestMessageSerializer.Pack(sendStream, msg)
                | SerializerType.Json -> 
                    let sw = new StreamWriter(sendStream)
                    Serializers.Json.serializer.Serialize(sw, msg, typeof<ScriptRequestMessage>)
                    sw.Flush()
                | _ -> raise(exn("Unknown serializer type"))
                MessageType.ScriptRequest
            | SessionRequest msg ->
                match serializerType with
                | SerializerType.MsgPack -> Serializers.MsgPack.sessionRequestMessageSerializer.Pack(sendStream, msg)
                | SerializerType.Json -> 
                    let sw = new StreamWriter(sendStream)
                    Serializers.Json.serializer.Serialize(sw, msg, typeof<SessionRequestMessage>)
                    sw.Flush()
                | _ -> raise(exn("Unknown serializer type"))                
                MessageType.SessionRequest
        
        // store the total length position
        let msgBodySize = (int)(sendStream.Length - 11L)
        // Seeks to where the body details should be placed
        sendStream.Seek(6L, SeekOrigin.Begin) |> ignore
        // Insert the body details
        sendStream |> insertMessageBodyDetails msgType msgBodySize
        // Rewind the send stream - ready to send
        sendStream.Seek (0L, SeekOrigin.Begin) |> ignore

    /// Prepares the socket for sending data (stops idle timer, connects to endpoint if neccessary).
    /// Also hooks on the idle timeout event for disposing the pooledsocket from the pool and memory
    let prepareSocket (socket:PooledSocket) =
        // Make sure the expire timer is paused so that it does not expose itself
        // while we communicate with Rexster
        socket.StopIdleTimer()
        // Connect to the server if we are not alredy
        if not socket.Connected then
            // Hook onto the expired/idle timeout event
            socket.IdleTimeoutElapsed.Add(fun (x) -> 
            (   
                // This means our socket has been in the pool for too long (idle timeout)
                // Make sure it will not be used again by removing it from the pool
                // Also close and dispose it to free up memory
                socketConnectionPool.RemoveItem(socket)
                socket.Disconnect()
                socket.Dispose()
            ))
            // Connect the socket
            socket.Connect remoteEndPoint

    /// Sends the message bytes using Socket to server
    /// Returns the response messageType and it's body bytes as a stream
    let sendMessage (serializerType:SerializerType) (message:RequestMessageType) =
        async {
            // Prepare our message
            use sendStream = new BufferPoolStream(socketSendBufferPool)
            
            sendStream |> fillSendStream message serializerType

            // Get socket and args from pool
            let! socket = socketConnectionPool.Pop()
            
            socket |> prepareSocket
            // Write the bytes
            do! socketSend socket sendStream

            // Read the response. Since we are returning this stream we cannot use the 'use' keyword
            let receiveStream = new BufferPoolStream(socketReceiveBufferPool)
            
            do! socketReceive socket receiveStream
            // Store our message type before we reset the messageData
            let messageType = socket.MessageData.MessageHeader.MessageType

            // Seek to after the header bytes (11) so that the next read will be the message
            receiveStream.Seek (11L, SeekOrigin.Begin) |> ignore

            // The messageData is ready for reuse
            socket.MessageData.Reset()

            // We are not using this socket anymore, start expire timer
            socket.StartIdleTimer()
            // Push socket and args back on pool
            socketConnectionPool.Push(socket)

            // The message type with the bytes
            return (messageType, receiveStream)
        }
    
    /// Create a SessionRequestMessage SessionOpen or SessionClose(with Id)
    let createSessionRequestMessage openOrClose =
        let msg = new SessionRequestMessage(username, password)
        match openOrClose with
        | SessionOpen ->
            msg.Meta.Add("graphName", graphName)
            msg.Meta.Add("graphObjName", "g")
            msg.Meta.Add("killSession", false)
            SessionRequest(msg)
        | SessionClose id ->
            msg.Meta.Add("killSession", true)
            msg.Session <- id
            SessionRequest(msg)

    /// Create a ScriptRequestMessage
    let createScriptRequestMessage script bindings sessionId =
        let msg = new ScriptRequestMessage(script, bindings |> bindingsToDic)
        if sessionId <> Guid.Empty then
            msg.Session <- sessionId
            msg.Meta.Add("inSession", true)
            msg.Meta.Add("isolate", false)
        else
            // If we are not in a session we must define graphName and objName
            // But if we are in a session setting these again will cause an error
            msg.Meta.Add("inSession", false)
            msg.Meta.Add("graphName", graphName)
            msg.Meta.Add("graphObjName", "g")
            msg.Meta.Add("isolate", true)
        msg.Meta.Add("transaction", true)
        msg.Meta.Add("console", false)
        ScriptRequest(msg)

    /// Construct a RexProClientException with the ErrorMessage from the ErrorResponseMessage
    let errorMessageResponseException (serializerType:SerializerType) receiveStream =
        match serializerType with
        | SerializerType.MsgPack -> Serializers.MsgPack.errorResponseMessageSerializer.Unpack receiveStream
        | SerializerType.Json -> 
            Serializers.Json.serializer.Deserialize(new StreamReader(receiveStream), typeof<obj[]>) :?> obj[]
            |> fun arr ->
                let msg = new ErrorResponseMessage()
                msg.Session <- Guid.Parse(arr.[0] :?> string)
                msg.Request <- Guid.Parse(arr.[1] :?> string)
                msg.ErrorMessage <- arr.[3] :?> string
                msg
        | _ -> raise(exn("Unknown serializer type"))
        |> fun errorMsg ->
            (new RexProClientException(errorMsg.ErrorMessage))
    
    /// Try to write the fatal exception to log (surrounded by try catch in case the logger itself is throwing the exception)
    let tryLogFatal (ex:exn) =
#if DEBUG
        try 
            log.fatal(sprintf "Message: %s\nStackTrace: %s" ex.Message ex.StackTrace)
            log.flush()
        with
        | _ -> ()
#else
        ()
#endif

    /// Try to write the error exception to log (surrounded by try catch in case the logger itself is throwing the exception)
    let tryLogError (ex:exn) =
#if DEBUG
        try 
            log.error(sprintf "Message: %s\nStackTrace: %s" ex.Message ex.StackTrace)
            log.flush()
        with
        | _ -> ()
#else
        ()
#endif

    /// Removes comment blocks, newlines and excess space
    let prepareScript script =
        Regex.Replace(script, "(/\*([^*]|[\r\n]|(\*+([^*/]|[\r\n])))*\*+/)", "")
        |> String.map (fun c -> if c <> '\r' && c <> '\n' && c <> '\t' then c else ' ')
        |> fun s -> s.Split([|' '|], StringSplitOptions.RemoveEmptyEntries)
        |> String.concat " "
        |> fun s -> s.Replace(" .", ".")


    /// Public properties and methods 
    /// ##########################################################################################################

    member val GraphName = graphName with get, set
    member val SessionId = Guid.Empty with get, set
    member val SerializerType = SerializerType.Json with get, set
    
    /// Open a new session, all consequent queries or executes will use this session Id until closeSession() is called
    member x.OpenSessionAsync() =
        async {
            if x.SessionId <> Guid.Empty then raise (RexProClientException("Already in a session"))

            try
                let msg = createSessionRequestMessage SessionOpen
                let! messageType, receiveStream =
                    createSessionRequestMessage SessionOpen
                    |> sendMessage x.SerializerType

                match messageType with
                | MessageType.SessionResponse -> 
                    match x.SerializerType with
                    | SerializerType.MsgPack -> Serializers.MsgPack.sessionResponseMessageSerializer.Unpack receiveStream
                    | SerializerType.Json -> 
                        Serializers.Json.serializer.Deserialize(new StreamReader(receiveStream), typeof<obj[]>) :?> obj[]
                        |> fun arr ->
                            let msg = new SessionResponseMessage()
                            msg.Session <- Guid.Parse(arr.[0] :?> string)
                            msg.Request <- Guid.Parse(arr.[1] :?> string)
                            msg
                    | _ -> raise(exn("Unknown serializer type"))
                    |> fun msg -> x.SessionId <- msg.Session
                | MessageType.ErrorResponse -> 
                    raise (errorMessageResponseException x.SerializerType receiveStream)
                | _ -> 
                    raise (RexProClientException(sprintf "Unexpected message type: %A" messageType))

                receiveStream.Dispose()

                return Some x.SessionId
            with
            | e when isCriticalException e ->
                tryLogFatal e
                return None
            | e -> 
                tryLogError e
                return None
        }

    /// Open a new session
    member x.OpenSession() =
        x.OpenSessionAsync() |> Async.RunSynchronously

    /// Close a session with Id
    member x.CloseSessionAsync() =
        async {
            // Raise exception if not in a session
            if x.SessionId = Guid.Empty then raise (RexProClientException("Not in a session"))

            try
                let! messageType, receiveStream =
                    createSessionRequestMessage (SessionClose x.SessionId)
                    |> sendMessage x.SerializerType

                let returnedSessionId =
                    match messageType with
                    | MessageType.SessionResponse -> 
                        match x.SerializerType with
                        | SerializerType.MsgPack -> Serializers.MsgPack.sessionResponseMessageSerializer.Unpack receiveStream
                        | SerializerType.Json -> 
                            Serializers.Json.serializer.Deserialize(new StreamReader(receiveStream), typeof<obj[]>) :?> obj[]
                            |> fun arr ->
                                let msg = new SessionResponseMessage()
                                msg.Session <- Guid.Parse(arr.[0] :?> string)
                                msg.Request <- Guid.Parse(arr.[1] :?> string)
                                msg
                        | _ -> raise(exn("Unknown serializer type"))
                        |> fun msg -> msg.Session
                    | MessageType.ErrorResponse -> 
                        raise (errorMessageResponseException x.SerializerType receiveStream)
                    | _ -> 
                        raise (RexProClientException(sprintf "Unexpected message type: %A" messageType))

                receiveStream.Dispose()

                if returnedSessionId <> Guid.Empty then
                    raise (RexProClientException(sprintf "Unexpected Session ID: [%s]" (returnedSessionId.ToString())))
        
                x.SessionId <- Guid.Empty
            with
            | e when isCriticalException e ->
                tryLogFatal e
            | e -> 
                tryLogError e
        }

    /// Close the current session
    member x.CloseSession() =
        x.CloseSessionAsync() |> Async.RunSynchronously
    
    /// Do a query async
    member x.QueryAsync<'a> (script:string) (bindings:list<string * _>) =
        async {
            try
                let preparedScript = prepareScript script
#if DEBUG
                log.debug(sprintf "query: %s" preparedScript)
                sw.Restart() // reset stopwatch (make sure it's 0)
#endif
                let! messageType, receiveStream =
                    createScriptRequestMessage preparedScript bindings x.SessionId
                    |> sendMessage x.SerializerType

                let result =
                    match messageType with
                    | MessageType.ScriptResponse -> 
                        match x.SerializerType with
                        | SerializerType.MsgPack -> Serializers.MsgPack.scriptResponseMessageSerializer.Unpack receiveStream
                        | SerializerType.Json ->                    
                            Serializers.Json.serializer.Deserialize(new StreamReader(receiveStream), typeof<obj[]>) :?> obj[]
                            |> fun arr ->
                                let msg = new ScriptResponseMessage()
                                msg.Session <- Guid.Parse(arr.[0] :?> string)
                                msg.Request <- Guid.Parse(arr.[1] :?> string)
                                msg.Results <- (arr.[3] :?> Newtonsoft.Json.Linq.JContainer) |> fun obj -> obj.ToObject(typeof<'a>)
                                msg
                        | _ -> raise(exn("Unknown serializer type"))
                        |> fun msg ->
                            QuerySuccess (msg.Results :?> 'a)
                    | MessageType.ErrorResponse -> 
                        QueryError (errorMessageResponseException x.SerializerType receiveStream)
                    | _ -> 
                        QueryError (RexProClientException(sprintf "Unexpected message type: %A" messageType))

                receiveStream.Dispose()
#if DEBUG
                sw.Stop();
                log.debug(sprintf "query took: %f ms" sw.Elapsed.TotalMilliseconds)
                log.flush()
#endif
                return result
            with
            | e when isCriticalException e ->
                tryLogFatal e
                return QueryError e
            | e -> 
                tryLogError e
                return QueryError e
        }

    /// Do a query
    member x.Query<'a> (script:string) (bindings:list<string * _>) =
        x.QueryAsync<'a> script bindings
        |> Async.RunSynchronously

    /// Do a execute async
    member x.ExecuteAsync (script:string) (bindings:list<string * _>) =
        async {
            try
                let preparedScript = prepareScript script
#if DEBUG
                log.debug(sprintf "execute: %s" preparedScript)
                sw.Restart()
#endif
                let! messageType, receiveStream =
                    createScriptRequestMessage preparedScript bindings x.SessionId
                    |> sendMessage x.SerializerType

                let result =
                    match messageType with
                    | MessageType.ScriptResponse ->
                        QuerySuccess ()
                    | MessageType.ErrorResponse -> 
                        QueryError (errorMessageResponseException x.SerializerType receiveStream)
                    | _ -> 
                        QueryError (RexProClientException(sprintf "Unexpected message type: %A" messageType))

                receiveStream.Dispose()
#if DEBUG
                sw.Stop();
                log.debug(sprintf "execute took: %f" sw.Elapsed.TotalMilliseconds)
                log.flush()
#endif
                return result
            with
            | e when isCriticalException e ->
                tryLogFatal e
                return QueryError e
            | e -> 
                tryLogError e
                return QueryError e
        }
    
    /// Do a execute
    member x.Execute (script:string) (bindings:list<string * _>) =
        x.ExecuteAsync script bindings
        |> Async.RunSynchronously

/// Convenience class for easier use of a session.
/// Use with the use keyword "use session = new RexProSession()"
type RexProSession(client:RexProClient) =
    
    do
        match client.OpenSession() with
        | Some id -> ()
        | None -> raise (RexProClientException("Failed to get Session ID"))

    interface IDisposable with 
        member x.Dispose() =
            client.CloseSession()

    member x.Dispose() = (x :> IDisposable).Dispose()

    member val SessionId = client.SessionId with get

    member x.Query<'a> (script:string) (bindings:list<string * _>) =
        client.Query<'a> script bindings

    member x.QueryAsync<'a> (script:string) (bindings:list<string * _>) =
        client.QueryAsync<'a> script bindings

    member x.Execute (script:string) (bindings:list<string * _>) =
        client.Execute script bindings

    member x.ExecuteAsync (script:string) (bindings:list<string * _>) =
        client.Execute script bindings

    new (host, port, graphName, username, password) = 
        let client = new RexProClient(host, port, graphName, username, password)
        new RexProSession(client)

    