module CASO.DB.Titan.RexPro.Messages

open System
open System.Collections
open System.Collections.Generic
open MsgPack.Serialization
open Newtonsoft.Json
open Newtonsoft.Json.Linq

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

[<JsonConverter(typeof<SessionResponseMessageConverter>)>]
type SessionResponseMessage() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Languages = [|"groovy"|] with get, set

and SessionResponseMessageConverter() = 
    inherit JsonConverter()
    
    override x.WriteJson(writer, value, serializer) =
        raise (NotImplementedException())
    
    /// TODO: Find another way to deserialize (reflection is slow)
    override x.ReadJson(reader, objectType, existingValue, serializer) =
        let msg = new SessionResponseMessage()
        let arr = JArray.ReadFrom(reader)

        msg.Session <- Guid.Parse(arr.[0].Value<String>())
        msg.Request <- Guid.Parse(arr.[1].Value<String>())

        msg :> obj

    override x.CanConvert(objectType) = true

[<JsonConverter(typeof<ScriptResponseMessageConverter>)>]
type ScriptResponseMessage<'a>() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    [<JsonProperty(Order = 2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val Results = Unchecked.defaultof<'a> with get, set
    [<MessagePackMember(4)>]
    member val Bindings = new Dictionary<string, obj>() with get, set

and ScriptResponseMessageConverter() = 
    inherit JsonConverter()
    
    override x.WriteJson(writer, value, serializer) =
        raise (NotImplementedException())
    
    /// TODO: Find another way to deserialize (reflection is slow)
    override x.ReadJson(reader, objectType, existingValue, serializer) =
        let msgGenericType = typedefof<ScriptResponseMessage<_>>
        let responseType = objectType.GetGenericArguments().[0]
        let msgType = msgGenericType.MakeGenericType([|responseType|])
        let msg = Activator.CreateInstance(msgType)
        let arr = JArray.ReadFrom(reader)

        let session = Guid.Parse(arr.[0].Value<String>())
        let request = Guid.Parse(arr.[1].Value<String>())
        let result = arr.[3].ToObject(responseType)

        msgType.GetProperty("Session").SetValue(msg, session)
        msgType.GetProperty("Request").SetValue(msg, request)
        msgType.GetProperty("Results").SetValue(msg, result)
        msg

    override x.CanConvert(objectType) = true

[<JsonConverter(typeof<ErrorResponseMessageConverter>)>]
type ErrorResponseMessage() =
    [<MessagePackMember(0)>]
    member val Session = Guid.Empty with get, set
    [<MessagePackMember(1)>]
    member val Request = Guid.NewGuid() with get, set
    [<MessagePackMember(2)>]
    member val Meta = new Dictionary<string, obj>() with get, set
    [<MessagePackMember(3)>]
    member val ErrorMessage = "" with get, set

and ErrorResponseMessageConverter() = 
    inherit JsonConverter()
    
    override x.WriteJson(writer, value, serializer) =
        raise (NotImplementedException())
    
    /// TODO: Find another way to deserialize (reflection is slow)
    override x.ReadJson(reader, objectType, existingValue, serializer) =
        let msg = new ErrorResponseMessage()
        let arr = JArray.ReadFrom(reader)

        msg.Session <- Guid.Parse(arr.[0].Value<String>())
        msg.Request <- Guid.Parse(arr.[1].Value<String>())
        msg.ErrorMessage <- arr.[3].Value<String>()

        msg :> obj

    override x.CanConvert(objectType) = true

