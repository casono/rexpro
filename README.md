RexPro client in F#
---

For now take a look at Testing.fsx for simple use of the client.
There is also a bunch of stuff I am going to improve/add.

See http://s3.thinkaurelius.com/docs/titan/0.5.0/ for Titan graph specific configuration.

##### TODO:
- Better connection pooling
- Editable properties for connection pool settings
- Cleaner code within client.fs (separate some logic etc.)
- Transaction support
- +++

---
NuGet: http://www.nuget.org/packages/CASO.DB.Titan.RexPro

---

Example of simple use. Count all vertices in graph
```c#
open CASO.DB.Titan.RexPro

let client = new RexProClient("127.0.0.1", 8184, "graph", "", "")

client.Query<int64> "g.V.count();" []
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

Example of a simple query for adding and returning the added user:
```c#
client.Query<User>
    "user = g.addVertex(['type': 'user', 'userName': userName]); user.map();" 
    ["userName", "Mike Lowrey" :> obj]
```

Example of adding a user but not returning any result
```c#
client.Execute
    "g.addVertex(['type': 'user', 'userName': userName]);" 
    ["userName", "Mike Lowrey" :> obj]
```

Example of retreiving a user by userName
```c#
client.Execute
    "g.V('type', 'user').has('userName', userName).map();" 
    ["userName", "Mike Lowrey" :> obj]
```

Example of session use
```c#
use session = new RexProSession("127.0.0.1", 8184, "graph", "", "")

match session.Execute "test = 5;" [] with
| QuerySuccess _ -> 
    match session.Query<int> "test;" [] with
    | QuerySuccess num -> printfn "test = %d" num
    | QueryError e -> printfn "Error: %s" e.Message
| QueryError e -> 
    printfn "Error: %s" e.Message
```
