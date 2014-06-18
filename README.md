RexPro client in F#
---

Just created this repo. More information on how to use the client is coming very soon.
For now take a look at Testing.fsx for simple use of the client.
There is also a bunch of stuff I am going to improve/add.

##### TODO:
- Better connection pooling
- Editable properties for connection pool settings
- Cleaner code within client.fs (separate some logic etc.)
- Transaction support
- +++

Please clone and do whatever you want with this code, but please share.

---

Example of simple use. Count all vertices in graph
```c#
open CASO.DB.Titan.RexPro

let client = new RexProClient("127.0.0.1", 8184, "graph", "", "")

client.query<int64> "g.V.count();" []
```

Both query and execute takes a string with the query and a list with bindings.
All bindings are tuples (string * obj)
Example: 
```c# 
[("userName", "Mike Lowrey" :> obj);]
```

Example of a simple data model:
```c#
[<DataContract>]
type User(username) =
    [<DataMember(Name = "userName")>]
    member val UserName = username with get, set
    new() = User("")
```

Before we could use this in our graph we would have to create the keys first. If not the option autotype is set.
Something like this in a gremlin query:
```
g.makeKey('type').dataType(String.class).make();
g.makeKey('userName').dataType(String.class).indexed(Vertex.class).unique().make();
```

Example of a simple query for adding and returning the added user:
```c#
client.query<User>
    "user = g.addVertex(['type': 'user', 'userName': userName]); user.map();" 
    ["userName", "Mike Lowrey" :> obj]
```

Example of adding a user but not returning any result
```c#
client.execute
    "g.addVertex(['type': 'user', 'userName': userName]);" 
    ["userName", "Mike Lowrey" :> obj]
```

Example of retreiving a user by userName
```c#
client.execute
    "g.V('type', 'user').has('userName', userName).map();" 
    ["userName", "Mike Lowrey" :> obj]
```

Example of session use
```c#
use session = new RexProSession("127.0.0.1", 8184, "graph", "", "")

match session.execute "test = 5;" [] with
| QuerySuccess _ -> 
    match session.query<int> "test;" [] with
    | QuerySuccess num -> printfn "test = %d" num
    | QueryError e -> printfn "Error: %s" e.Message
| QueryError e -> 
    printfn "Error: %s" e.Message
```
