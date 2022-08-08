#time "on"
#r "nuget: Akka.FSharp, 1.4.25"
#r "nuget: Akka.Fsharp"
#r "nuget: Akka.Remote"

#nowarn

open System
open System.Security.Cryptography
open Akka.Actor
open Akka.Configuration
open Akka.FSharp
open System.Diagnostics

// Ask user the number of zeroes required in Bitcoin hash
//printf "Enter the number of Leading Zeroes : "
//let no = System.Console.ReadLine() |> int
let no = fsi.CommandLineArgs.[1] |> int
printfn "Number of zeroes are : %i" no

// Ask user the starting string of the bitcoin
//printf "UFID : "
//let ufid = System.Console.ReadLine()
let ufid = fsi.CommandLineArgs.[2] 
printfn "String will start hashing with : %s" ufid

let workload = fsi.CommandLineArgs.[3] |> int
printfn "Workload (Jobs performed by actor before coming to the master) : %i" workload

let coreCount = fsi.CommandLineArgs.[4] |> int
printfn "Number of cores to be used : %i" coreCount

let coinCount = fsi.CommandLineArgs.[5] |> int 
printfn "Coins to be mined : %i" coinCount

printfn "------------------------------"
let mutable ref = null
let mutable stopWatch = System.Diagnostics.Stopwatch

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

// Configuring master's TCP port for clients to communicate
let masterConfiguration = 
    ConfigurationFactory.ParseString(
        @"akka {
            actor {
                provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
            }
            remote {
                helios.tcp {
                    port = 9001
                    hostname = ""10.20.89.8""
                }
            }
            # OPTION : DEBUG,INFO,WARNING
            loglevel : ""OFF""
            
        }")

// Exposing TCP ports for the above configuration with the name 'RemoteFSharp'
let system = ActorSystem.Create("RemoteFSharp", masterConfiguration)

type Messages = 
    | InitiateBitCoinMiner
    | InitiateMaster
    | Getcoin 
    | Coin of string
    | Success of string*string
    | RemoteCoinRequest of IActorRef
    | RemoteSuccess of string*string*IActorRef
    | Abort

let mutable a = 0
let mutable k = 0

// An actor to compute bitcoins and check the leading zeroes
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
                        mailbox.Sender() <! Success(s,hash)
                        notfound <- false
                if notfound then
                    mailbox.Sender() <! Getcoin
            |   _ -> printfn "Undefined Message! May abort the task!"
            return! bitcoinLoop()
        }
    bitcoinLoop()
   
let mutable remoteMiners : IActorRef list = []

let Master (mailbox:Actor<_>) = 
    let mutable stringFound: bool = false;
    let mutable count = 0
    let rec loop() =
        actor {
            let! msg = mailbox.Receive()
            match msg with
            | InitiateMaster ->
                                let miners = [for i in 1 .. coreCount do yield (spawn system ("BitcoinActor"+i.ToString()) BitcoinMiner )]
                                for i in 0 .. coreCount-1  do
                                    miners.Item( i|>int) <! InitiateBitCoinMiner
            | Getcoin ->    
                            if stringFound then
                                mailbox.Context.System.Terminate() |> ignore
                            else
                                mailbox.Sender() <! Coin(ufid+","+workload.ToString())
            | Success (str, hash) ->
                            count <- count + 1
                            printfn "%A    %A" str hash
                            if count = coinCount then
                                stringFound <- true
                                remoteMiners |> List.iter(  fun remoterMiner -> remoterMiner <! "completed")
                                mailbox.Context.System.Terminate() |> ignore
                            else 
                                 mailbox.Sender() <! Coin(ufid+","+workload.ToString())
            | RemoteCoinRequest(remoteMiner) ->
                            remoteMiner <! "UFID, "+ufid+", "+string(no)+","+workload.ToString()
            | RemoteSuccess(str,hash,remoteMiner) ->
                                stringFound <- true
                                printfn "%A    %A" str hash
                                remoteMiners |> List.iter(  fun remoterMiner -> remoterMiner <! "completed")
                                mailbox.Context.System.Terminate() |> ignore
            |   _ -> printfn "Undefined Message! May abort the task!"
            return! loop()
        }
    loop()

let MasterRef = spawn system "Master" Master


let communicator = 
    spawn system "server"
    <| fun mailbox ->
        let rec loop() =
            actor{
                let! msg = mailbox.Receive()
                ref <- mailbox.Sender()
                printfn "%s" msg
                let message = msg.Split ','
                if msg.CompareTo("RemoteCoinRequest")=0 then
                    printfn "Request from Remote Miners"
                    remoteMiners <- mailbox.Sender() :: remoteMiners
                    printfn "Received Mining Request from %A" (mailbox.Sender().ToString())
                    MasterRef <! RemoteCoinRequest(mailbox.Sender())
                else
                    printfn "Success from Remote Miner!"
                    MasterRef <! RemoteSuccess(message.[0],message.[1],mailbox.Sender())
                return! loop()
            }
        loop()

MasterRef <! InitiateMaster
system.WhenTerminated.Wait()