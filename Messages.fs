module CASO.DB.Titan.RexPro.Messages

open System
open System.Collections.Generic
open MsgPack.Serialization

// Messages: https://github.com/tinkerpop/rexster/wiki/RexPro-Messages
// session, script, error

type SessionRequestMessage(username, password) =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty.ToByteArray() with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid().ToByteArray() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Username = username with get, set
    [<MessagePackMember(4)>]
    member val Password = password with get, set
    new() = SessionRequestMessage("", "")

type ScriptRequestMessage(script:string, bindings:Dictionary<string, obj>) =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty.ToByteArray() with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid().ToByteArray() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val LanguageName = "groovy" with get, set
    [<MessagePackMember(4)>]
    member val Script = script with get, set
    [<MessagePackMember(5)>]
    member val Bindings = bindings with get, set
    new() = ScriptRequestMessage("", new Dictionary<string, obj>())

type SessionResponseMessage() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty.ToByteArray() with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid().ToByteArray() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Languages = [|"groovy"|] with get, set

type ScriptResponseMessage<'a>() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty.ToByteArray() with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid().ToByteArray() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Results = Unchecked.defaultof<'a> with get, set
    [<MessagePackMember(4)>]
    member val Bindings = new Dictionary<string, obj>() with get, set

type ErrorResponseMessage() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty.ToByteArray() with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid().ToByteArray() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val ErrorMessage = "" with get, set