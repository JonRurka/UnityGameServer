using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnityGameServer {
    public class UnityServer<T> : ServerBase where T : UnityServer<T> {
        public static T Instance { get; private set; }    

        public UnityServer(string[] args) : base(args) {
            Instance = (T)this;
        }
    }
}
