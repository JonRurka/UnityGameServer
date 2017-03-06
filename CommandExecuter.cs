using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using UnityGameServer.Networking;

namespace UnityGameServer {
    public class CommandExecuter {
        public static CommandExecuter Instance { get; private set; }
        
        private Dictionary<string, CommandFunction> _cmdTable = new Dictionary<string, CommandFunction>();
        private Dictionary<string, ConsoleCommand> _cmdDescription = new Dictionary<string, ConsoleCommand>();
        private List<string> _history = new List<string>();

        private string _input = string.Empty;
        private int padAmount = 0;
        private int _historyIndex = 0;

        public bool Enabled { get; private set; }

        public CommandExecuter() {
            Instance = this;
            Enabled = true;
            LoadCommands(this);
        }

        public void Update() {
            try {
                if (!Console.KeyAvailable)
                    return;
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (!Enabled)
                    return;
                if (key.Key == ConsoleKey.Backspace) {
                    if (_input.Length > 0) {
                        _input = _input.Remove(_input.Length - 1);
                        Logger.InputStr = _input;
                    }
                }
                else if (key.Key == ConsoleKey.Enter) {
                    if (_input != string.Empty) {
                        string tmpStr = _input;
                        _input = string.Empty;
                        Logger.InputStr = _input;
                        _history.Add(tmpStr);
                        _historyIndex = _history.Count;
                        ExecuteCommand(tmpStr);
                    }
                }
                else if (key.Key == ConsoleKey.LeftArrow)
                    Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                else if (key.Key == ConsoleKey.RightArrow)
                    Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
                else if (key.Key == ConsoleKey.UpArrow) {
                    if (_history.Count > 0) {
                        if (_historyIndex - 1 >= 0) {
                            _historyIndex--;
                            _input = _history[_historyIndex];
                            Logger.InputStr = _input;
                        }
                    }
                }
                else if (key.Key == ConsoleKey.DownArrow) {
                    if (_history.Count > 0) {
                        if (_historyIndex + 1 < _history.Count) {
                            _historyIndex++;
                            _input = _history[_historyIndex];
                            Logger.InputStr = _input;
                        }
                        else {
                            _input = string.Empty;
                            Logger.InputStr = _input;
                            _historyIndex = _history.Count;
                        }
                    }
                    else {
                        _input = string.Empty;
                        Logger.InputStr = _input;
                        _historyIndex = _history.Count;
                    }
                }
                else {
                    _input += key.KeyChar;
                    Logger.InputStr = _input;
                }
            }
            catch (Exception e) {
                Logger.LogError("Error in command loop.");
                Logger.LogError("{0}: {1}\n{2}", e.GetType(), e.Message, e.StackTrace);
            }
        }

        public void LoadCommands(object target) {
            List<ConsoleCommand> attributeCommands = new List<ConsoleCommand>(GetAttributeCommands(target));
            for (int i = 0; i < attributeCommands.Count; i++) {
                RegisterCommand(attributeCommands[i]);
                if (attributeCommands[i].Command.Length > padAmount)
                    padAmount = attributeCommands[i].Command.Length;
            }
        }

        public ConsoleCommand[] GetAttributeCommands(object target) {
            List<ConsoleCommand> commands = new List<ConsoleCommand>();
            MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++) {
                ConsoleCommand commandAttribute = (ConsoleCommand)Attribute.GetCustomAttribute(methods[i], typeof(ConsoleCommand));
                if (commandAttribute != null) {
                    CommandFunction function = null;
                    if (methods[i].IsStatic)
                        function = (CommandFunction)Delegate.CreateDelegate(typeof(CommandFunction), methods[i]);
                    else
                        function = (CommandFunction)Delegate.CreateDelegate(typeof(CommandFunction), target, methods[i]);
                    if (function != null) {
                        commandAttribute.Callback = function;
                        commands.Add(commandAttribute);
                    }
                }
            }
            return commands.ToArray();
        }

        public void RegisterCommand(ConsoleCommand command) {
            _cmdTable[command.Command.ToLower()] = command.Callback;
            _cmdDescription[command.Command.ToLower()] = command;
        }

        public void UnregisterCommand(string commandString) {
            _cmdTable.Remove(commandString.ToLower());
            _cmdDescription.Remove(commandString.ToString());
        }

        public string[] Commands() {
            string[] commands = new string[_cmdTable.Keys.Count];
            _cmdTable.Keys.CopyTo(commands, 0);
            return commands;
        }

        public string ExecuteCommand(string command) {
            string result = string.Empty;
            try {
                Logger.Print("> {0}", command);
                command = command.Trim();
                if (!string.IsNullOrEmpty(command)) {
                    string[] args = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string cmd = args[0].ToLower();
                    if (_cmdTable.ContainsKey(cmd)) {
                        result = _cmdTable[cmd](new CommandContext(CommandContext.ContextType.Console, null), args).ToString();
                        if (result != string.Empty)
                            Logger.Print(result);
                    }
                    else {
                        Logger.LogError("Command not found: {0}", args[0]);
                        result = "Command not found: " + args[0];
                    }
                }
            }
            catch (Exception e) {
                Logger.LogError("{0}\n{1}", e.Message, e.StackTrace);
                result = "error: " + e.Message;
            }
            return result;
        }

        public string UserExecuteCommand(AsyncServer.SocketUser user, string command) {
            string result = string.Empty;
            command = command.Trim();
            if (!string.IsNullOrEmpty(command)) {
                string[] args = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string cmd = args[0].ToLower();
                if (_cmdTable.ContainsKey(cmd) && _cmdDescription[cmd].Permission <= user.Permission) {
                    try {
                        result = _cmdTable[cmd](new CommandContext(CommandContext.ContextType.User, user), args).ToString();
                    }
                    catch(Exception ex) {
                        result = string.Format("Error: {0} - {1}", ex.GetType(), ex.Message);
                    }
                    Logger.Log("{0}: exec \"{1}\"", user.SessionToken, command);
                    if (result != string.Empty) {
                        Logger.Print(result);
                    }
                }
                else {
                    result = "Command not found: " + args[0];
                }
            }
            return result;
        }

        public bool CommandExists(string command) {
            return _cmdDescription.ContainsKey(command);
        }

        public ConsoleCommand GetCommand(string command) {
            if (CommandExists(command))
                return _cmdDescription[command];
            return default(ConsoleCommand);
        }

        public void Close() {
            Enabled = false;
            _cmdTable.Clear();
            _cmdDescription.Clear();
            _history.Clear();
        }

        // COMMANDS

        [ConsoleCommand(1, "help", "[command]", "prints command help.")]
        private object Help_CMD(CommandContext context, params string[] args) {
            StringBuilder output = new StringBuilder();
            if (args.Length == 1) {
                output.AppendLine(string.Format("Commands ({0}):", _cmdTable.Count.ToString()));
                foreach (string key in _cmdTable.Keys) {
                    if (context.Type == CommandContext.ContextType.Console || _cmdDescription[key].Permission <= context.Caller.Permission) {
                        output.AppendLine(string.Format("  {0} : {1}", key.PadRight(padAmount, ' '), _cmdDescription[key].Description_small));
                    }
                }
            }
            else if (args.Length == 2) {
                if (_cmdTable.ContainsKey(args[1])) {
                    if (context.Type == CommandContext.ContextType.Console || _cmdDescription[args[1]].Permission <= context.Caller.Permission) {
                        output.AppendLine(string.Format(" - Command: {0} {1}", _cmdDescription[args[1]].Command, _cmdDescription[args[1]].Command_args));
                        output.AppendLine(string.Format(" - Short description: {0}", _cmdDescription[args[1]].Description_small));
                        if (!string.IsNullOrEmpty(_cmdDescription[args[1]].Description_Long))
                            output.AppendLine(string.Format(" - Long description: {0}", _cmdDescription[args[1]].Description_Long));
                    }
                    else {
                        return "Invalid permission level.";
                    }
                }
                else
                    return "Command not found: " + args[1];

            }
            else {
                return "To many arguments.";
            }
            return output.ToString();
        }

        [ConsoleCommand(1, "clear", "", "Clear the screen.")]
        private object Clear_CMD(CommandContext context, params string[] args) {
            Logger.Clear();
            return "";
        }

        [ConsoleCommand(1, "exit", "", "save and close server.")]
        private object Exit_CMD(CommandContext context, params string[] args) {
            ServerBase.BaseInstance.Stop();
            return "";
        }

        [ConsoleCommand(1, "processes", "", "List async queue processes")]
        private object Processes_CMD(CommandContext context, params string[] args) {
            string[] processes = TaskQueue.GetProcessNames();
            for (int i = 0; i < processes.Length; i++) {
                Logger.Log("  " + processes[i]);
            }
            return "";
        }
    }
}
