﻿namespace flack 
    open System
    open System.Net
    open System.Net.Sockets
    open System.Collections.Generic
    open System.Collections.Concurrent
    open System.Threading
    open SocketExtensions
            
    type TcpListener(maxaccepts, maxsends, maxreceives, size, port, backlog) as this =

        let createTcpSocket() =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)

        let createListener (ip:IPAddress, port, backlog) =
            let s = createTcpSocket()
            s.Bind(new IPEndPoint(ip, port))
            s.Listen(backlog)
            s

        let listeningSocket = createListener( IPAddress.Loopback, port, backlog)

        let initPool (maxinpool, callback) =
            let pool = new BlockingCollection<SocketAsyncEventArgs>(maxinpool:int)
            let rec loop n =
                match n with
                | x when x < maxinpool -> 
                    let saea = new SocketAsyncEventArgs()
                    saea.Completed |> Observable.add callback 
                    pool.Add saea
                    loop (n+1)
                | _ -> ()
            loop 0
            pool

        let acceptPool = initPool (maxaccepts, this.acceptcompleted)
        let newConnection socket = new Connection (maxreceives, maxsends, size, socket)
        let testMessage = Array.init<byte> 128 (fun _ -> 1uy)
        let header = Array.init<byte> 1 (fun _ -> 1uy)
        let mutable disposed = false
        
        //mutable state from original
        let mutable anyErrors = false
        let mutable requestCount = 0
        let mutable numWritten = 0

        //async code from original
        let asyncWriteStockQuote(connection:Connection) = async {
            do! Async.Sleep 100
            connection.Send(testMessage)
            Interlocked.Increment(&numWritten) |> ignore }
        
        //async code from original
        let asyncServiceClient (client: Connection) = async {
            client.Send(header)
            while true do
                do! asyncWriteStockQuote(client) }

        let startSending connection = 
            Async.Start (async { 
                try 
                    use _holder = connection
                    do! asyncServiceClient connection 
                with e -> 
                    if not(anyErrors) then
                        anyErrors <- true
                        Console.WriteLine("server ERROR")
                    raise e
                } )

        let reportConnections() = 
            Interlocked.Increment(&requestCount) |> ignore
            if requestCount % 50 = 0 then 
                requestCount |> printfn "%A Clients accepted"

        let cleanUp() = 
            if not disposed then
                disposed <- true
                listeningSocket.Shutdown(SocketShutdown.Both)
                listeningSocket.Disconnect(false)
                listeningSocket.Close()

        member this.acceptcompleted (args : SocketAsyncEventArgs) =
            try
                match args.LastOperation with
                | SocketAsyncOperation.Accept ->
                    match args.SocketError with
                    | SocketError.Success -> 
                        listeningSocket.AcceptAsyncSafe( this.acceptcompleted, acceptPool.Take())
                        //create new connection
                        let connection = newConnection args.AcceptSocket
                        connection.Start()

                        //update stats
                        reportConnections()

                        //async start of messages to client
                        startSending connection

                        //remove the AcceptSocket because we will be reusing args
                        args.AcceptSocket <- null 
                    | _ -> args.SocketError.ToString() |> printfn "socket error on accept: %s"         
                | _ -> args.LastOperation |> failwith "Unknown operation, should be accept but was %a"            
            finally
                acceptPool.Add(args)

        member this.start () =   
            listeningSocket.AcceptAsyncSafe( this.acceptcompleted, acceptPool.Take())
            while true do
            Thread.Sleep 1000
            let count = Interlocked.Exchange(&numWritten, 0)
            count |> printfn "Quotes per sec: %A"

        member this.Close() =
            cleanUp()

        interface IDisposable with
            member this.Dispose() = cleanUp()