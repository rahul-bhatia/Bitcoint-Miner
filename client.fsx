#r "nuget: Akka.FSharp, 1.4.25"
#r "nuget: Akka.Fsharp"
#r "nuget: Akka.Remote"

open System
open System.Security.Cryptography
open System.IO
open System.Text
open Akka.Actor
open Akka.Configuration
open Akka.FSharp
open System
open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets

let localIPs =
    let networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                            |> Array.filter(fun iface -> iface.OperationalStatus.Equals(OperationalStatus.Up))

    let addresses = seq {
        for iface in networkInterfaces do
            for unicastAddr in iface.GetIPProperties().UnicastAddresses do
                yield unicastAddr.Address}

    addresses
    |> Seq.filter(fun addr -> addr.AddressFamily.Equals(AddressFamily.InterNetwork))
    |> Seq.filter(IPAddress.IsLoopback >> not)
    |> Seq.head

let mutable ufid = "32471390"
let mutable no = 2

let localhost = localIPs.ToString()
printfn "Local IP : %s" localhost

//let serverIp = Console.ReadLine() 
let serverIP = fsi.CommandLineArgs.[1]  
printfn "Server IP : %s" serverIP

let url = "akka.tcp://RemoteFSharp@"+serverIP+":9001/user/server"
let rand()=
    let r=Random()
    let chars = Array.concat([[|'a'..'z'|];[|'A'..'B'|]])
    let sz = Array.length chars in 
    String(Array.init 15 (fun _ -> chars.[r.Next sz]))

let getSha256 (str:String) =
    System.Text.Encoding.ASCII.GetBytes(str)
    |> SHA256.Create().ComputeHash
    |> Array.map (fun (x:byte) -> System.String.Format("{0:x2}",x))
    |> String.concat String.Empty

let check(hash:String)=
    let index = hash |> Seq.tryFindIndex(fun x -> x <> '0')
    index.IsSome && index.Value = no

let clientConfiguration = 
    ConfigurationFactory.ParseString(
        @"akka {
            actor {
                provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
            }
            remote {
                helios.tcp {
                    port = 8888
                    hostname = "+localhost+"
                }
            }
        }")

let system = ActorSystem.Create("CLientFsharp",clientConfiguration)

type Messages = 
    | InitiateBitCoinMiner
    | InitiateCoordinator
    | Getcoin
    | Coin of string
    | Success of string*string

let mutable a = 0
let mutable k = 0
let mutable workload = 0
let BitcoinMiner (mailbox:Actor<_>) = 
    let rec bitcoinLoop() =
        actor {
            let! msg = mailbox.Receive()
            match msg with
            | InitiateBitCoinMiner ->
                                    mailbox.Sender()<! Getcoin
            | Coin(str) ->
                let getBytes(s:string) = System.Text.Encoding.ASCII.GetBytes s 
                let composedFunction = getBytes 
                let ip=str.Split(',')
                let mutable s = null
                s <- ip.[0] 
                k <- int(ip.[1])
                a <- 0
                let mutable notfound = true
                for i in a .. k do
                    s <- ip.[0] + rand()
                    let hash=getSha256(s)   
                    if check(hash) then
                        printfn "**"
                        mailbox.Sender() <! Success(s,hash)
                        notfound <- false
                if notfound then
                    mailbox.Sender() <! Getcoin
            |   _ -> printfn "Undefined Message! May abort the task!"
            return! bitcoinLoop()
        }
    bitcoinLoop()

let Coordinator (mailbox:Actor<_>) = 
    let mutable stringFound: bool = false;
    let rec loop() =
        actor {
            let! msg = mailbox.Receive()
            match msg with
            | InitiateCoordinator ->
                                let miners = [for i in 1L..4L do yield (spawn system ("BitcoinActor"+i.ToString()) BitcoinMiner)]
                                for i in 0L .. 3L do
                                    miners.Item( i|>int) <! InitiateBitCoinMiner
            | Getcoin ->    
                            if stringFound then
                                mailbox.Context.System.Terminate() |> ignore
                            else
                                mailbox.Sender() <! Coin(ufid+","+workload.ToString())
            | Success (str, hash) ->
                            stringFound <- true
                            printfn "%A    %A" str hash
                            let remoteServer = system.ActorSelection(url)
                            remoteServer <! str+","+hash
                            mailbox.Context.System.Terminate() |> ignore
            |   _ -> printfn "Undefined Message! May abort the task!"
            return! loop()
        }
    loop()

let mutable remoteWorkDone = false
let commlink =
    spawn system "client"
    <| fun mailbox ->
        let rec loop() =
            actor{
                let! msg = mailbox.Receive()
                let response = msg|> string
                let command = (response).Split ","
                if command.[0].CompareTo("init")=0 then
                    let remoteServer = system.ActorSelection(url)
                    printfn "%s" (remoteServer.ToString())
                    remoteServer <! "RemoteCoinRequest"
                elif command.[0].CompareTo("UFID")=0 then 
                    no <- (int command.[2])
                    ufid <- command.[1]
                    workload <- int command.[3]
                    let coordinatorRef = spawn system "RemoteCoordinator" Coordinator
                    coordinatorRef <! InitiateCoordinator
                elif response.CompareTo("completed")=0 then 
                    printfn "Terminating"
                    system.Terminate() |> ignore
                else
                    printfn "Success from Remote Miner!"
                    
                return! loop()
            }
        loop()
commlink <! "init"
system.WhenTerminated.Wait()