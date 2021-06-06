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


        // TODO - NRP - uncomment this for state saving       
        // protected override void PostRestart(Exception reason) {
        //     log.Info("Restarting Server " + Self);
            
        //     List<string> serializedServerList = ReadFromXmlFile<List<string>>(Self.Path.Name + "ServerList.xml");;
        //     foreach (string id in serializedServerList){
        //         ServerList.Add(extendedSys.Provider.ResolveActorRef(id));
        //     }

        //     state = ReadFromXmlFile<State>(Self.Path.Name + "State.xml");
        //     // state.setNumServers(ServerList.Count);
        //     state.setLogger(log);

        //     log.Info("term: " + state.currentTerm);
        //     state.printLog();

        //     electionTimeout = rnd.Next(1, 10);
        //     log.Info("Election timeout set for " + electionTimeout + " seconds");
        //     resetTimer();

        //     // become follower
        //     log.Info("FOLLOWER");
        //     Become(Follower);
        // }
       
        protected override void PreStart() {
            foreach (ActorPath actor in ServerList) {
                Context.System.ActorSelection(actor).Tell(new Identify(Self));
                Context.System.ActorSelection(actor).Tell("Oy");
            }
            state.initializeLog();
            state.initIndexTracking();
            state.setLogger(log);
        }

        // TODO - NRP - uncomment for state saving
        // protected override void PostStop() {
        //     log.Info("Saving off state before shutdown");

        //     WriteToXmlFile(Self.Path.Name + "State.xml", state);

        //     // Serialization serialization = Context.System.Serialization;
        //     List<string> serializedServerList = new List<string>();
        //     foreach (IActorRef actor in ServerList){
        //         serializedServerList.Add(Serialization.SerializedActorPath(actor));
        //     }
        //     WriteToXmlFile(Self.Path.Name + "ServerList.xml", serializedServerList);
        // }

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

                // Start the initial heartbeat
                electionTimeout = rnd.Next(1, 10);
                log.Info("Election timeout set for " + electionTimeout + " seconds");
                resetTimer();

                // become follower
                log.Info("FOLLOWER");
                Become(Follower);
            }
            
        }

        protected void Follower(object message) {
            switch (message) {
                case AppendEntriesRPC rpc:
                    updateLogEntries(rpc);
                    break;

                case RequestVoteRPC rpc:
                    // log safety check
                    if (state.lastLogTerm() > rpc.lastLogTerm ||
                        state.lastLogTerm() == rpc.lastLogTerm && state.lastLogIndex() > rpc.lastLogIndex) {
                        Sender.Tell(new RequestVoteRPCReply(state.currentTerm, false));
                    }

                    // perform term check
                    else if (rpc.term < state.currentTerm) {
                        Sender.Tell(new RequestVoteRPCReply(state.currentTerm, false));
                    }

                    else {
                        // update term to candidate term 
                        if (rpc.term > state.currentTerm) {
                            state.votedFor = null;
                            state.currentTerm = rpc.term;
                            log.Info("Term: " + state.currentTerm);
                        }

                        // cast vote
                        if (state.votedFor == null) {
                            log.Info("Voting for " + Sender);
                            state.votedFor = Sender;
                            Sender.Tell(new RequestVoteRPCReply(state.currentTerm, true));
                        }

                        // reset timer
                        log.Info("Voting for new candidate - reset timer");
                        resetTimer();
                    }
                    break;

                case Timer t:
                    startElection();
                    break;

                default:
                    handleMessage(message, Sender);
                    break;
            }
        }

        protected void Candidate(object message) {
            switch (message) {

                case Timer t:
                    // get a new random timeout to reduce chances of infinite elections
                    electionTimeout = rnd.Next(1, 10);
                    log.Info("Election timeout set to " + electionTimeout);
                    startElection();
                    break;

                case AppendEntriesRPC rpc:
                    if (rpc.term > state.currentTerm) {
                        // reset timer
                        log.Info("AppendEntries from new leader - reset timer");
                        resetTimer();

                        // cancel your candidacy
                        state.votedFor = null;
                        votes = 0;

                        // update term 
                        state.currentTerm = rpc.term;
                        log.Info("Term: " + state.currentTerm);

                        // update log entries
                        updateLogEntries(rpc);

                        // become follower
                        log.Info("CANDIDATE -> FOLLOWER");
                        Become(Follower);
                    }
                    break;

                case RequestVoteRPC rpc:
                    respondToVoteReq(rpc);
                    break;

                case RequestVoteRPCReply reply:
                    if (reply.term > state.currentTerm) {
                        // cancel your candidacy
                        state.votedFor = null;
                        votes = 0;

                        // update term 
                        state.currentTerm = reply.term;
                        log.Info("Term: " + state.currentTerm);

                        // become follower
                        log.Info("CANDIDATE -> FOLLOWER");
                        Become(Follower);
                    }

                    else if (reply.term == state.currentTerm && reply.voteGranted) {
                        votes ++;
                        log.Info("Vote from " + Sender);

                        if (votes >= (ServerList.Count / 2) + 1) {
                            // reset next and match indexes
                            state.resetVolatileLeaderState();

                            // assume leadership
                            log.Info("CANDIDATE -> LEADER");
                            sendHeartBeat();
                            Become(Leader);
                        }
                    }

                    break;

                default:
                    handleMessage(message, Sender);
                    break;
            }
        }

        protected void Leader(object message) {
            switch (message) {
                case Timer t:
                    // deliver heartbeat
                    sendHeartBeat();
                    break;

                case RequestVoteRPC rpc:
                    respondToVoteReq(rpc);
                    break;

                case AppendEntriesRPC rpc:
                    if (Sender == Self) {
                        break;
                    }
                    if(rpc.term > state.currentTerm) {
                        // update term 
                        state.currentTerm = rpc.term;
                        log.Info("Term: " + state.currentTerm);

                        // become follower
                        log.Info("LEADER -> FOLLOWER");
                        Become(Follower);
                    }
                    break;

                case AppendEntriesRPCReply reply:
                    int i = ServerList.IndexOf(Sender.Path);
                    if (!reply.success) {
                        state.nextIndex[i] --;
                        log.Info("append for " + Sender + " failed, next_index: " + state.nextIndex[i]);

                    } else {
                        if (state.nextIndex[i] < state.log.Count) {
                            state.nextIndex[i] ++;
                        }
                        if (state.matchIndex[i] < state.lastLogIndex()) {
                            state.matchIndex[i] ++;       
                        }                        
                    }
                    log.Info("next_index: " + state.nextIndex[i] + ", matchIndex: " + state.matchIndex[i] + ", commitIndex: " + state.commitIndex + ", lastApplied: " + state.lastApplied);

                    if (state.lastLogIndex() >= state.nextIndex[i]) {
                        log.Info("Sending update to " + Sender);
                        List<LogItem> relEntries = state.log.GetRange(state.nextIndex[i] + 1, 1);
                        tell(ServerList[i],new AppendEntriesRPC(state.currentTerm, Self, state.nextIndex[i], state.log[state.nextIndex[i]].term, relEntries, state.commitIndex));
                        // ServerList[i].Tell(new AppendEntriesRPC(state.currentTerm, Self, state.nextIndex[i], state.log[state.nextIndex[i]].term, relEntries, state.commitIndex));
                    }

                    updateCommitIndex();
                    updateStateMachine(true);

                    break;

                case ClientRequest cmd:
                    // update log
                    log.Info("Received command from client");

                    // check to see if we have processed a similar cmd
                    List<LogItem> appliedItems = state.log.GetRange(1, state.lastApplied);
                    foreach(LogItem item in appliedItems) {
                        if (item.id == cmd.id) {
                            log.Info("Rejecting request - already applied");
                            Sender.Tell(item.value);
                            break;
                        }
                    }

                    int prevLogIndex = state.lastLogIndex();
                    int prevLogTerm; 

                    // add relevant info to cmd object
                    state.addLogItem(cmd, Serialization.SerializedActorPath(Sender), prevLogIndex);
                    
                    // inform serverlist of client updates
                    for (int j=0; j< ServerList.Count; j++) {
                        // TODO - NRP - double check this one
                        if (ServerList[j] == Self.Path) {
                            continue;
                        }
                        // not sure why it can't just be >
                        if (prevLogIndex >= state.nextIndex[j]) {
                            prevLogIndex = state.nextIndex[j];
                        }
                        prevLogTerm = state.log[prevLogIndex].term;

                        // get all relevant entries
                        // List<LogItem> relEntries = state.log.GetRange(prevLogIndex + 1, state.log.Count - (prevLogIndex + 1));
                        
                        // log.Info(ServerList[j] + " prevLogIndex: " + prevLogIndex);
                        List<LogItem> relEntries = state.log.GetRange(prevLogIndex + 1, 1);
                        // log.Info("sending " + relEntries.Count + " entities to follower");
                        tell(ServerList[j], new AppendEntriesRPC(state.currentTerm, Self, prevLogIndex, prevLogTerm, relEntries, state.commitIndex));
                        // ServerList[j].Tell(new AppendEntriesRPC(state.currentTerm, Self, prevLogIndex, prevLogTerm, relEntries, state.commitIndex));
                    } 
                    state.printLog();
                    break;

                default:
                    handleMessage(message, Sender);
                    break;
            }
        }
        
        protected void AddPeer(IActorRef sender) {
            if (Sender == Self && !ServerList.Contains(Sender.Path)) {
                ServerList.Add(Sender.Path);
                log.Warning($"Added Sender {Sender.Path} to Serverlist, Count: {ServerList.Count}");
            }
        }

        protected void startElection() {
            log.Info("FOLLOWER -> CANDIDATE");
    
            // increment term
            state.currentTerm += 1;
            log.Info("Term: " + state.currentTerm);

            // vote for self
            state.votedFor = Self;
            votes = 1;
            log.Info("DEBUG - votes had: " + votes + ", needed: " + ((ServerList.Count / 2) + 1));
            if (votes >= (ServerList.Count / 2) + 1) {
                // reset next and match indexes
                state.resetVolatileLeaderState();

                // assume leadership
                log.Info("CANDIDATE -> LEADER");
                sendHeartBeat();
                Become(Leader);
                return;
            }
            
            // reset timer
            resetTimer();

            // request votes from others
            // foreach (IActorRef actor in ServerList) {
            //     actor.Tell(new RequestVoteRPC(state.currentTerm, Self, state.log.Count - 1, state.log[state.log.Count - 1].term));
            // } 
            foreach (ActorPath actor in ServerList) {
                tell(actor, new RequestVoteRPC(state.currentTerm, Self, state.log.Count - 1, state.log[state.log.Count - 1].term));
            }

            Become(Candidate);
        }
        protected void sendHeartBeat() {
            // log.Info("Heartbeat");
            // foreach (IActorRef actor in ServerList) {
            //     actor.Tell(new AppendEntriesRPC(state.currentTerm, Self, state.log.Count - 1, state.log[state.log.Count - 1].term, new List<LogItem>(), state.commitIndex));
            // }
            foreach (ActorPath actor in ServerList) {
                tell(actor, new AppendEntriesRPC(state.currentTerm, Self, state.log.Count - 1, state.log[state.log.Count - 1].term, new List<LogItem>(), state.commitIndex));
            }
            resetTimer();
        }
        protected void respondToVoteReq(RequestVoteRPC rpc) {
            if (Sender == Self) {
                return;
            }

            if (state.currentTerm > rpc.term) {
                Sender.Tell(new RequestVoteRPCReply(state.currentTerm, false));
            }
            
            // log safety  and term check
            else if (state.lastLogTerm() > rpc.lastLogTerm ||
                state.lastLogTerm() == rpc.lastLogTerm && state.lastLogIndex() > rpc.lastLogIndex) {
                Sender.Tell(new RequestVoteRPCReply(state.currentTerm, false));
            }

            else if (state.currentTerm == rpc.term && state.votedFor != null) {
                Sender.Tell(new RequestVoteRPCReply(state.currentTerm, false));
            }

            else {
                // cancel your candidacy
                state.votedFor = null;
                votes = 0;

                // update term 
                state.currentTerm = rpc.term;
                log.Info("Term: " + state.currentTerm);

                // vote for sender
                log.Info("Voting for " + Sender);
                Sender.Tell(new RequestVoteRPCReply(state.currentTerm, true));

                // become follower
                log.Info("FOLLOWER");
                Become(Follower);

                // reset timer
                // log.Info("Voting for new candidate - reset timer");
                resetTimer();
            }
        }
        protected void resetTimer() {
            // log.Info("reset timer");
            Timers.StartSingleTimer("election_timeout", new Timer(), TimeSpan.FromSeconds(electionTimeout));
        }
        protected void updateLogEntries(AppendEntriesRPC rpc) {
            if (rpc.term < state.currentTerm || rpc.prevLogIndex > state.log.Count - 1) {
                if (rpc.term < state.currentTerm) {log.Warning("Mismatching terms, cannot accept AppendEntries");};
                if (rpc.prevLogIndex > state.log.Count - 1) {
                    log.Warning("Logs are out of date, log update unsuccessful");
                    log.Info("prevLogIndex: " + rpc.prevLogIndex + ", log.Count: " + (state.log.Count - 1));
                };
                rpc.leaderId.Tell(new AppendEntriesRPCReply(state.currentTerm, false));
                return;
            }

            // delete conflicting log entry and all following entries
            if (rpc.prevLogTerm != state.log[rpc.prevLogIndex].term) {
                log.Warning("Logs are out of date, log update unsuccessful");
                log.Info("Term of log entry " + rpc.prevLogTerm + " don't match");
                rpc.leaderId.Tell(new AppendEntriesRPCReply(state.currentTerm, false));
            }

            // append any new entries not already in the log
            else if (rpc.prevLogTerm == state.log[rpc.prevLogIndex].term) {
                int i, j;
                for (i=0; i < rpc.entries.Count; i++) {
                    if (rpc.prevLogIndex + i + 1 > state.log.Count - 1) {
                        // log.Info("all entites match existing logs");
                        break;
                    }
                    if (state.log[rpc.prevLogIndex + i + 1].term != rpc.entries[i].term) {
                        // entities don't match, delete entity and all following entities
                        state.removeLogs(rpc.prevLogIndex + i + 1);
                        break;
                    }
                }

                // add in all new entries
                for (j=i; j<rpc.entries.Count; j++) {
                    // log.Info("adding at index " + (rpc.prevLogIndex + j + 1));
                    state.addLogItem(rpc.entries[j]);
                }
                if (rpc.entries.Count > 0) {state.printLog();};

                // update commit index
                if (rpc.leaderCommitIndex > state.commitIndex) {
                    state.commitIndex = Math.Min(rpc.leaderCommitIndex, rpc.prevLogIndex + j + 1);
                }
                
                // check if state machine should be updated
                updateStateMachine(false);
                // log.Info("CommitIndex: " + state.commitIndex + ", LastAppliedIndex: " + state.lastApplied );

                lastKnownLeader = rpc.leaderId;
                rpc.leaderId.Tell(new AppendEntriesRPCReply(state.currentTerm, true));
            }

            // received AppendEntries from current leader - reset timer
            resetTimer();
        }
        protected void updateCommitIndex() {
            List<int> sortedMatchIndexes = new List<int>(state.matchIndex);
		    sortedMatchIndexes.Sort((a, b) => b.CompareTo(a));

            // only commit if the entry is from the current term
            if (state.log[sortedMatchIndexes[sortedMatchIndexes.Count/2 - 1]].term == state.currentTerm) {
                state.commitIndex = Math.Max(state.commitIndex, sortedMatchIndexes[sortedMatchIndexes.Count/2 - 1]);
                // log.Info("Commit Index: " + state.commitIndex);
            } else {
                // log.Info("DEBUG - matchIndex: " + printList(state.matchIndex));
                // log.Info("DEBUG - index: " + sortedMatchIndexes[sortedMatchIndexes.Count/2 - 1]);
                log.Info("Cannot commit because log is from previous term " + state.log[sortedMatchIndexes[sortedMatchIndexes.Count/2 - 1]].term + " " + state.currentTerm);
            }
        }   
        protected void updateStateMachine(Boolean leader) {
            if (state.commitIndex > state.lastApplied) {
                state.lastApplied ++;
                LogItem logToApply = state.log[state.lastApplied];
                switch(logToApply.command.function) {
                    case "inc":
                        state.stateVariable += logToApply.command.amount;
                        break;
                    case "dec":
                        state.stateVariable -= logToApply.command.amount;
                        break;
                    case "eq":
                        state.stateVariable = logToApply.command.amount;
                        break;
                    default:
                        break;
                }
                log.Info("Applied log id " + logToApply.id + ", counter value: " + state.stateVariable);
                state.log[state.lastApplied].value = state.stateVariable;
                IActorRef sender = extendedSys.Provider.ResolveActorRef(state.log[state.lastApplied].serializedSender);
                if (leader) {
                    sender.Tell(state.stateVariable);
                }
            }
        }

        protected void handleMessage(object message, IActorRef sender) {
            switch (message) {
                // case Close s:
                //     log.Info("END msg received");
                //     Context.Stop(Self);
                //     break;

                case ClientRequest c:
                    log.Warning("Command recieved, needs to be passed to the leader");
                    if (lastKnownLeader != null) {
                        sender.Tell(lastKnownLeader);
                    }
                    break;

                case Exception ex:
                    log.Error("Server received Kill Message");
                    throw ex;

                default:
                    log.Warning("Default: " + message.GetType());
                    break;
            }
        }
        protected string printList(List<int> lis) {
            string s = "[ ";
            foreach(int item in lis) {
                s += (item + " ");
            }
            s += "]";
            return s;
            // log.Info(s);
        }

        protected void tell (ActorPath actor, object message) {
            Context.System.ActorSelection(actor).Tell(message);
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
