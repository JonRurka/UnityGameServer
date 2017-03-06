using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityGameServer.Networking;

namespace UnityGameServer {
    public class CommandContext {
        public enum ContextType {
            Console,
            User,
        }

        public AsyncServer.SocketUser Caller;
        public ContextType Type;

        public CommandContext(ContextType type, AsyncServer.SocketUser caller) {
            Type = type;
            Caller = caller;
        }
    }

    public class ConsoleCommand : Attribute {
        public int Permission { get; set; }
        public string Command { get; set; }
        public string Command_args { get; set; }
        public string Description_small { get; set; }
        public string Description_Long { get; set; }
        public CommandFunction Callback { get; set; }

        public ConsoleCommand(int permission, string command, string commandArgs, string descSmall, string descLarge = "") {
            Permission = permission;
            Command = command;
            Command_args = commandArgs;
            Description_small = descSmall;
            Description_Long = descLarge;
        }
    }

    public delegate object CommandFunction(CommandContext context, params string[] args);
}
