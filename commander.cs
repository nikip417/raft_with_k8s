using System;
using Akka.Actor;
using Akka.Event;
using Akka.Cluster.Tools.Client;
using System.Collections.Immutable;
using System.Collections.Generic;

namespace Raft {
    public class Command {
        public string function;
        public int amount;
    }

    public class ClientRequest {
        public int id;
        public IActorRef sender;
        public Command command;

        public ClientRequest(int id, Command command){
            this.id = id;
            this.command = command;
        }
    }

    public class CommanderActor : UntypedActor {
        private ILoggingAdapter log = Context.GetLogger();
        int nextClientReqId = 0;
        IActorRef client;
        string peer = "akka.tcp://demo@actordemo-0.actordemo.default.svc.cluster.local:4053";

        public CommanderActor () {
            Console.WriteLine("Initing COmmander");
            Context.System.Settings.InjectTopLevelFallback(ClusterClientReceptionist.DefaultConfig());
            var initialContacts = new List<ActorPath>(){ ActorPath.Parse(peer)}.ToImmutableHashSet();
            var settings = ClusterClientSettings.Create(Context.System).WithInitialContacts(initialContacts);
            client = Context.System.ActorOf(ClusterClient.Props(settings), "client");
        }
        protected override void OnReceive(object message) {
            switch (message) {
                // For client testing
                case int x:
                    Console.WriteLine($"Sending {x} to the cluster");
                    Command cmd = new Command();
                    cmd.function = "inc";
                    cmd.amount = x;
                    client.Tell(new ClusterClient.SendToAll(peer, new ClientRequest(nextClientReqId, cmd)));

                    nextClientReqId ++;
                    break;

                default:
                    log.Error("Not sure why I ended up in the default");
                    log.Info("" + message);
                    break;
            }
        }
    }
    

    class Program {
        private static ExtendedActorSystem sys = (ExtendedActorSystem) ActorSystem.Create("system");
        static void Main() {
            IActorRef cmder = sys.ActorOf(Props.Create<CommanderActor>(), "Commander");

            Boolean done = false;
            while(!done) {
                int n;
                var num = Console.ReadLine();
                if (num ==  "") {
                    done = true;
                    break;
                }
                    
                if(int.TryParse(num, out n)) {
                    Console.WriteLine("You are really gonna try this");
                    cmder.Tell(n);
                }
            }
        }
    }    

}
