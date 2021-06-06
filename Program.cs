using System;
using System.IO;
using System.Net;
using Akka.Actor;
using Akka.Cluster;
using Akka.Configuration;
using Akka.Serialization;
using Akka.Event;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Xml.Serialization;


using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;

namespace ActorDemo
{
    public class State {
        // variable the state machine is managing
        public int stateVariable = 0;
        // TODO - NRP - update this (numServers)
        public int numServers = 2;

        // Persistent state on all servers
        private ILoggingAdapter logger;
        public int currentTerm;
        
        [XmlIgnore]
        public IActorRef votedFor;
        public List<LogItem> log = new List<LogItem>();

        // Volitile State on all servers
        public int commitIndex;
        public int lastApplied;

        // Volitile state on leaders
        // re-init after an election
        public List<int> nextIndex = new List<int>();
        public List<int> matchIndex = new List<int>();

        public State() {
            currentTerm = 0;
            votedFor = null;
            commitIndex = 0;
            lastApplied = 0;
        }
        
        public void initializeLog() {
            LogItem logItem = new LogItem();
            logItem.term = 0;
            logItem.command = null;
            this.log.Add(logItem);
        }
        
        public void initIndexTracking() {
            for (int i = 0; i < this.numServers; i++) {
                this.nextIndex.Add(this.log.Count + 1);
                this.matchIndex.Add(0);
            }
        }

        public void setLogger(ILoggingAdapter logger) {
            this.logger = logger;
        }

        public int lastLogIndex(){
            return this.log.Count - 1;
        }

        public int lastLogTerm(){
            return this.log[this.lastLogIndex()].term;
        }

        public void addLogItem(LogItem log) {
            this.log.Add(log);
        }
        
        public void addLogItem(ClientRequest cr, string sender, int prevLogIndex) {
            LogItem logItem = new LogItem();
            logItem.term = this.currentTerm;
            logItem.command = cr.command;
            logItem.id = cr.id;
            logItem.prevLogIndex = prevLogIndex;
            logItem.serializedSender = sender;
            this.log.Add(logItem);
        }
        
        public void printLog() {
            string s = "Printing Logs: [ ";
            foreach (LogItem log in this.log) {
                s = s + log.term + " ";
            }
            logger.Info(s + "]");
        }
        
        public void removeLogs(int index){
            logger.Info("Removed logs from index " + index + " onwards");
            this.printLog();
            int numEntries = this.log.Count - index;
            this.log.RemoveRange(index, numEntries);
        }

        public void resetVolatileLeaderState() {
            for (int i = 0; i < this.nextIndex.Count; i++) {
                this.nextIndex[i] = this.log.Count;
                // according to the simulation they should all be re-inited to 0
                this.matchIndex[i] = 0;
            }
        }
    }
    
    public class RequestVoteRPC {
        public int term;
        public IActorRef candidateId;
        public int lastLogIndex;
        public int lastLogTerm;

        public RequestVoteRPC(int term, IActorRef candidateId, int lastLogIndex, int lastLogTerm){
            this.term = term;
            this.candidateId = candidateId;
            this.lastLogIndex = lastLogIndex;
            this.lastLogTerm = lastLogTerm;
        }
    }
    
    public class RequestVoteRPCReply {
        public int term;
        public Boolean voteGranted;

        public RequestVoteRPCReply(int currentTerm, Boolean voteGranted) {
            this.term = currentTerm;
            this.voteGranted = voteGranted;
        }
    }
    
    public class AppendEntriesRPC {
        public int term;
        public IActorRef leaderId;
        public int prevLogIndex;
        public int prevLogTerm;
        public List<LogItem> entries; 
        public int leaderCommitIndex;

        public AppendEntriesRPC(int term, IActorRef leaderID, int prevLogIndex, int prevLogTerm, List<LogItem> relevantEntries, int leaderCommitIndex){
            this.term = term;
            this.leaderId = leaderID;
            this.prevLogIndex = prevLogIndex;
            this.prevLogTerm = prevLogTerm;
            this.entries = relevantEntries;
            this.leaderCommitIndex = leaderCommitIndex;
        }
    }

    public class AppendEntriesRPCReply {
        public int term;
        public Boolean success;
        public AppendEntriesRPCReply(int currentTerm, Boolean success) {
            this.term = currentTerm;
            this.success = success;
        }
    }
   
    public class Command {
        public string function;
        public int amount;
    }

    public class Timer {}
    
    public class LogItem {
        public int term;
        public Command command;
        public int id;
        public int prevLogIndex;
        public int value;
        public string serializedSender;
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

    public class Server : UntypedActor, IWithTimers {
        private ILoggingAdapter log = Context.GetLogger();
        private List<ActorPath> ServerList = new List<ActorPath>();
        private int Port { get; }
        public ITimerScheduler Timers { get; set; }
        // private IActorRef Commander = null;
        // private List<IActorRef> ServerList = new List<IActorRef>();
        private State state;
        private int votes = 0;
        private Random rnd = new Random();
        private int electionTimeout;
        public ExtendedActorSystem extendedSys = (ExtendedActorSystem) Context.System;
        public IActorRef lastKnownLeader = null;
        public Server(List<string> peers, int port) {
            Port = port;
            state = new State();
            foreach (string peer in peers) {
                ServerList.Add(ActorPath.Parse($"{peer}/user/pingpong"));
            }

        }

        public static void WriteToXmlFile<T>(string filePath, T objectToWrite, bool append = false) where T : new()
        /** this was taken from https://stackoverflow.com/questions/6115721/how-to-save-restore-serializable-object-to-from-file **/
        {
            TextWriter writer = null;
            try
            {
                var serializer = new XmlSerializer(typeof(T));
                writer = new StreamWriter(filePath, append);
                serializer.Serialize(writer, objectToWrite);
            }
            finally
            {
                if (writer != null)
                    writer.Close();
            }
        }
        
        public static T ReadFromXmlFile<T>(string filePath) where T : new()
        /** this was taken from https://stackoverflow.com/questions/6115721/how-to-save-restore-serializable-object-to-from-file **/
        {
            TextReader reader = null;
            try
            {
                var serializer = new XmlSerializer(typeof(T));
                reader = new StreamReader(filePath);
                return (T)serializer.Deserialize(reader);
            }
            finally
            {
                if (reader != null)
                    reader.Close();
            }
        }

       
        protected override void PostRestart(Exception reason) {
            // TODO - NRP  - uncomment this!
            // log.Info("Restarting Server " + Self);
            
            // List<string> serializedServerList = ReadFromXmlFile<List<string>>(Self.Path.Name + "ServerList.xml");;
            // foreach (string id in serializedServerList){
            //     ServerList.Add(extendedSys.Provider.ResolveActorRef(id));
            // }

            // state = ReadFromXmlFile<State>(Self.Path.Name + "State.xml");
            // // state.setNumServers(ServerList.Count);
            // state.setLogger(log);

            // log.Info("term: " + state.currentTerm);
            // state.printLog();

            // electionTimeout = rnd.Next(1, 10);
            // log.Info("Election timeout set for " + electionTimeout + " seconds");
            // resetTimer();

            // // become follower
            // log.Info("FOLLOWER");
            // Become(Follower);
        }
       
        protected override void PreStart() {
            foreach (ActorPath actor in ServerList) {
                Context.System.ActorSelection(actor).Tell(new Identify(Self));
                Context.System.ActorSelection(actor).Tell("Oy");
            }
            state.initializeLog();
            state.initIndexTracking();
            state.setLogger(log);
        }

        protected override void PostStop() {
            log.Info("Saving off state before shutdown");

            WriteToXmlFile(Self.Path.Name + "State.xml", state);

            // Serialization serialization = Context.System.Serialization;
            List<string> serializedServerList = new List<string>();
            foreach (IActorRef actor in ServerList){
                serializedServerList.Add(Serialization.SerializedActorPath(actor));
            }
            WriteToXmlFile(Self.Path.Name + "ServerList.xml", serializedServerList);
        }

        protected override void OnReceive(object message) {
            switch (message) {
                // case Start dir:
                //     log.Info("Starting Server");
                //     ServerList = dir.serverList;
                //     state.initializeLog();
                //     state.setNumServers(ServerList.Count);
                //     state.setLogger(log);

                //     // Start the initial heartbeat
                //     electionTimeout = rnd.Next(1, 10);
                //     log.Info("Election timeout set for " + electionTimeout + " seconds");
                //     resetTimer();

                //     // become follower
                //     log.Info("FOLLOWER");
                //     Become(Follower);

                //     break;
                
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
                    AddPeer(Sender);
                    break;
                case IActorRef other:
                    log.Warning($"Received IActorRef from {Sender}");
                    AddPeer(Sender);
                    break;
                case string s:
                    log.Warning($"Received {s} from {Sender}");
                    AddPeer(Sender);
                    break;
            }

            if (ServerList.Count == state.numServers) {           
                log.Info("Aware of all nodes");

                // // Start the initial heartbeat
                // electionTimeout = rnd.Next(1, 10);
                // log.Info("Election timeout set for " + electionTimeout + " seconds");
                // resetTimer();

                // // become follower
                // log.Info("FOLLOWER");
                // Become(Follower);
            }
            
        }

        protected void AddPeer(IActorRef sender) {
            if (Sender == Self && !ServerList.Contains(Sender.Path)) {
                ServerList.Add(Sender.Path);
                log.Warning($"Added Sender {Sender.Path} to Serverlist, Count: {ServerList.Count}");
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
