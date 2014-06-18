module internal CASO.DB.Titan.RexPro.ObjectPool

open System

//Agent alias for MailboxProcessor
type Agent<'T> = MailboxProcessor<'T>

type PoolMessage<'a> =
    | Pop of AsyncReplyChannel<'a>
    | Push of ('a)
    | Remove of ('a)
    | Clear of AsyncReplyChannel<List<'a>>

/// Object pool representing a reusable pool of objects
type ObjectPool<'a when 'a : equality>(generate: unit -> 'a, initialPoolCount, maxPoolCount) = 

    let initial = List.init initialPoolCount (fun (x) -> generate())
    let usedCount = ref 0

    /// Removes an item from the list based on index. Will traverse the whole list
    /// TODO: find a faster better way to remove an item, maybe use a mutable List
    let rec remove index list =
        match index, list with
        | 0, item::xlist -> xlist
        | i, item::xlist -> item::remove (i - 1) xlist
        | i, [] -> failwith "index out of range"

    let agent = Agent.Start(fun inbox ->
        let rec loop(list) = async {
            let! msg = inbox.Receive()
            match msg with
            | Pop(reply) -> 
                let newList = 
                    match list with
                    | item::xlist ->
                        reply.Reply(item);
                        xlist
                    | [] as empty ->
                        if maxPoolCount <> 0 && !usedCount >= maxPoolCount then
                            raise (new exn(sprintf "ObjectPool maxPoolCount reached! (%d)" maxPoolCount))
                        reply.Reply(generate());
                        empty
                incr usedCount
                return! loop(newList)
            | Push(item) -> 
                decr usedCount
                return! loop(item::list)
            | Remove(item) ->
                match List.tryFindIndex (fun listItem -> listItem.Equals(item)) list with
                | Some index -> 
                    return! loop(remove index list)
                | None -> 
                    return! loop(list)
            | Clear(reply) -> 
                reply.Reply(list)
                usedCount := 0
                return! loop(List.empty<'a>) 
            }
        loop(initial)
        )

    do
        // Check arguments
        if initialPoolCount > 0 && initialPoolCount > maxPoolCount then invalidArg "initialPoolCount" "Cannot be more than maxPoolCount"

    /// Current items in pool
    member val Count = !usedCount with get
    member val MaxPoolCount = maxPoolCount with get

    /// Clears the object pool, returning all of the data that was in the pool.
    member x.ToListAndClear() = 
        agent.PostAndAsyncReply(Clear)
    /// Puts an item into the pool
    member x.Push(item) = 
        agent.Post(Push(item))
    /// Gets an item from the pool or if there are none present use the generator
    member x.Pop(item) = 
        agent.PostAndAsyncReply(Pop)

    member x.RemoveItem(item) =
        agent.Post(Remove(item))
