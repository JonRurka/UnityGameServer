using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnityGameServer.Networking {
    public class Data {
        public Protocal Type;
        public byte command;
        public byte[] Buffer;
        public string Input;

        public Data(Protocal type, byte cmd, byte[] data) {
            Type = type;
            command = cmd;
            Buffer = data;
            Input = Encoding.UTF8.GetString(Buffer);
        }
    }
}
