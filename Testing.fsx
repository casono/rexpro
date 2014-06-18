// Paths to our external packages (dll's)
#I "packages\\MsgPack.Cli.0.4.4\\lib\\net40-client\\";;

// Load the assemblies
#r "System";;
#r "MsgPack";;
#r "System.Runtime.Serialization";;

// Load our source files
#load "Messages.fs";;
#load "LogAgent.fs";;
#load "ObjectPool.fs";;
#load "BufferPoolStream.fs";;
#load "Client.fs";;

// Performance measuring
#time "on";;

open CASO.DB.Titan.RexPro;;

let client = new RexProClient("127.0.0.1", 8184, "graph", "", "");;

/// Test 1 connection
let test1() =
    match client.query<int64> "g.V.count();" [] with
    | QuerySuccess count -> printfn "Vertex count: %d" count
    | QueryError e -> printfn "Error: %s" e.Message

/// Test 20 concurrent connections
let test2() =
    let func() = client.queryAsync<int64> "g.V.count();" []

    seq { for i = 0 to 20 do yield func() }
    |> Async.Parallel
    |> Async.Ignore
    |> Async.RunSynchronously

/// Test 100 async connections in sequence
let test3() =
    for i = 0 to 100 do 
        client.queryAsync<int64> "g.V.count();" []
        |> Async.Ignore
        |> Async.StartImmediate

/// Test 100 connections in sequence
let test4() =
    for i = 0 to 100 do 
        match client.query<int64> "g.V.count();" [] with
        | QuerySuccess count -> printfn "Vertex count: %d" count
        | QueryError e -> printfn "Error: %s" e.Message

/// Test session
let test5() =
    use session = new RexProSession("127.0.0.1", 8184, "graph", "", "")

    match session.execute "test = 5;" [] with
    | QuerySuccess _ -> 
        match session.query<int> "test;" [] with
        | QuerySuccess num -> printfn "test = %d" num
        | QueryError e -> printfn "Error: %s" e.Message
    | QueryError e
        | QueryError e -> printfn "Error: %s" e.Message