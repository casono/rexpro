module CASO.DB.Titan.RexPro.Messages

open System
open System.Collections
open System.Collections.Generic
open MsgPack.Serialization
open Newtonsoft.Json

// Messages: https://github.com/tinkerpop/rexster/wiki/RexPro-Messages
// session, script, error
[<JsonArray>]
type SessionRequestMessage(username, password) =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Username = username with get, set
    [<MessagePackMember(4)>]
    member val Password = password with get, set
    interface IEnumerable with
        member x.GetEnumerator() =
            let arr = new ArrayList()
            arr.Add(x.Session) |> ignore
            arr.Add(x.Request) |> ignore
            arr.Add(x.Meta) |> ignore
            arr.Add(x.Username) |> ignore
            arr.Add(x.Password) |> ignore
            arr.GetEnumerator()
    new() = SessionRequestMessage("", "")

[<JsonArray>]
type ScriptRequestMessage(script:string, bindings:Dictionary<string, obj>) =
    [<MessagePackMember(0)>]
    [<JsonProperty(Order = 0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val LanguageName = "groovy" with get, set
    [<MessagePackMember(4)>]
    member val Script = script with get, set
    [<MessagePackMember(5)>]
    member val Bindings = bindings with get, set
    interface IEnumerable with
        member x.GetEnumerator() =
            let arr = new ArrayList()
            arr.Add(x.Session) |> ignore
            arr.Add(x.Request) |> ignore
            arr.Add(x.Meta) |> ignore
            arr.Add(x.LanguageName) |> ignore
            arr.Add(x.Script) |> ignore
            arr.Add(x.Bindings) |> ignore
            arr.GetEnumerator()
    new() = ScriptRequestMessage("", new Dictionary<string, obj>())

type SessionResponseMessage() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Languages = [|"groovy"|] with get, set

type ScriptResponseMessage() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    [<JsonProperty(Order = 2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Results = null with get, set
    [<MessagePackMember(4)>]
    member val Bindings = new Dictionary<string, obj>() with get, set

type ErrorResponseMessage() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val ErrorMessage = "" with get, set