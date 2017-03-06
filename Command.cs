using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TankFootballServer {
    public class Command : Attribute {
        public byte command;
        public ServerCMD opcode;

        public Command(byte command) {
            this.command = command;
            this.opcode = (ServerCMD)command;
        }

        public Command(ServerCMD opcode) {
            this.opcode = opcode;
            this.command = (byte)opcode;
        }
    }
}
