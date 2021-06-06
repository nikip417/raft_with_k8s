using System;
using System.IO;
using System.Net;
using Akka.Actor;
using Akka.Cluster;
using Akka.Configuration;
using Akka.Event;
using System.Threading.Tasks;
using System.Collections.Generic;

using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;

namespace ActorDemo
{
    public class Server : UntypedActor {
        private ILoggingAdapter log = Context.GetLogger();
        private List<ActorPath> ServerList = new List<ActorPath>();
        private int Port { get; }
        public Server(List<string> peers, int port) {
            Port = port;
            foreach (string peer in peers) {
                ServerList.Add(ActorPath.Parse($"{peer}/user/pingpong"));
            }

        }
        protected override void PreStart() {
            foreach (ActorPath actor in ServerList) {
                Context.System.ActorSelection(actor).Tell(new Identify(Self));
                Context.System.ActorSelection(actor).Tell("Oy");
            }
        }
        protected override void OnReceive(object message) {
            switch (message) {
                case ClusterEvent.CurrentClusterState st:
                    foreach (var m in st.Members) {
                        log.Info($"Heard about prior member joining at {m.Address}");
                    }
                    break;
                case ClusterEvent.MemberJoined ev:
                    log.Info($"Heard about member joining at {ev.Member.Address}");
                    var sel = Context.System.ActorSelection(ActorPath.Parse($"{ev.Member.Address}/user/pingpong"));
                    sel.Tell(new Identify(Self));
                    sel.Tell("Hello");
                    break;
                case Identify id:
                    log.Warning($"Received Identify from {Sender}");
                    break;
                case IActorRef other:
                    log.Warning($"Received IActorRef from {Sender}");
                    break;
                case string s:
                    log.Warning($"Received {s} from {Sender}");
                    if (Sender == Self && !ServerList.Contains(Sender.Path)) {
                        ServerList.Add(Sender.Path);
                        log.Warning($"Added Sender {Sender.Path} to Serverlist, Count: {ServerList.Count}");
                    }
                    break;
            }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Joining cluster...");
            string def = File.ReadAllText("akka.hocon");
            // Akka.NET says it replaces the public hostname with the appropriate DNS... but that doesn't really happen. So I've rigged the docker run command to set HOSTNAME to the DNS name on the private network...
            string hostset = def.Replace("BADHOST",
             Environment.GetEnvironmentVariable("HOSTNAME") +
             (Environment.GetEnvironmentVariable("AKKA_SEED_PATH") != null
             ? "."+Environment.GetEnvironmentVariable("AKKA_SEED_PATH")
             : ""));
            Console.WriteLine(hostset);
            var config = ConfigurationFactory.ParseString(hostset);
            var ipAddress = config.GetString("akka.remote.dot-netty.tcp.public-hostname", Dns.GetHostName());
            var port = config.GetInt("akka.remote.dot-netty.tcp.port");
            var selfAddress = $"akka.tcp://demo@{ipAddress}:{port}";
            var sys = ActorSystem.Create("demo", config);
            Console.WriteLine($"joined at {selfAddress}");

            //Console.WriteLine("Setting up cluster management/observability");
            //var pbm = PetabridgeCmd.Get(sys);
            //pbm.RegisterCommandPalette(ClusterCommands.Instance);
            //pbm.Start();

            var seeds = config.GetStringList("akka.cluster.seed-nodes");

            Console.WriteLine("Starting actor...");
            IActorRef supervisor = sys.ActorOf(Props.Create<Server>(seeds, port), "pingpong");
            Cluster.Get(sys).Subscribe(supervisor, ClusterEvent.SubscriptionInitialStateMode.InitialStateAsSnapshot, typeof(ClusterEvent.MemberJoined));

            await sys.WhenTerminated;

        }
    }
}
