/*

#############################################
EasyServerPoll by Xenne
v1.0.4
#############################################

This plugin makes it possible to create a server-wide poll
where your players can vote on a question with predefined answers.

When closing the poll, the results will be shown and can be pushed to
Discord using a webhook. You can add the webhook URL to the config.

Usage: /startpoll <question> <option1> <option2> <option3> <option4> <option5>
Example: /startpoll "Do you like this plugin?" "Yes, it's awesome!" "It's okay" "No, it's horrible!"

*/

using System.Collections.Generic;
using System.Text;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using Oxide.Core.Libraries;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
 
namespace Oxide.Plugins
{
    [Info("EasyServerPoll", "Xenne", "1.0.4")]
    public class EasyServerPoll : RustPlugin
    {

        // Class to store the configuration
        private class PluginConfig
        {
            public bool sendPollResultToDiscord { get; set; }
            public string DiscordWebhookUrl { get; set; }
            public int DefaultPollClosingTime { get; set; } // time in seconds
            public bool forceGlobalVoteScreenOnPollCreate { get; set; }
            public bool automaticPollEnd { get; set; }
            public bool sendReminder { get; set; }
            public int sendReminderTime { get; set; }
            public int timeTrackerInterval { get; set; }
            public string messagePrefix { get; set; }
            public string messageSuffix { get; set; }
        }

        // Class to store active poll data
        private class PollData
        {
            public bool pollActive;
            public string pollQuestion;
            public int remainingSeconds;
            public Dictionary<string, int> pollOptions;
            public Dictionary<ulong, string> playerVotes;
        }

        // Helper class to contain the poll history data
        private class PollHistoryData
        {
            // Dictionary for storing the old polls
            public Dictionary<int, PollHistory> pollHistory;
        }

        // Class for storing a old poll
        private class PollHistory
        {
            public int pollId;           
            public string pollQuestion;
            public Dictionary<string, int> pollOptions;
            
        }

        
        private PluginConfig config;
        private PollHistoryData pollHistoryData;
        private string pollQuestion = null;
        private bool pollActive = false;
        private Dictionary<ulong, string> playerVotes = new Dictionary<ulong, string>();
        private Dictionary<string, int> pollOptions = new Dictionary<string, int>();
        private int remainingSeconds = 0;



        // Discord webhook
        private string DiscordWebhookUrl => config.DiscordWebhookUrl;

        // Timers
        private Timer pollTimer;
        private Timer reminderTimer;
        private Timer timeTracker;

        // Permissions strings
        private const string permissionPollCreate = "EasyServerPoll.create";
        private const string permissionPollEnd = "EasyServerPoll.endpoll";
        private const string permissionVote = "EasyServerPoll.vote";
        private const string permissionShowResults = "EasyServerPoll.viewresults";

        // The timetracker runs every * seconds to check whether the poll has ended
        // or not.
        private int timeTrackerInterval => config.timeTrackerInterval;




        #region Server

        private void Init()
        {
            // Load poll data
            LoadPollData();

            // Register permissions
            permission.RegisterPermission(permissionPollCreate, this);
            permission.RegisterPermission(permissionVote, this);
            permission.RegisterPermission(permissionPollEnd, this);
            permission.RegisterPermission(permissionShowResults, this);

        }


        void Unload()
        {

            timeTracker?.Destroy();
            reminderTimer?.Destroy();
            SavePollData();
        }

        #endregion



        #region Config

        protected override void LoadDefaultConfig()
        {
            PrintWarning("No configuration file found. Creating default one...");

            config = new PluginConfig
            {
                DiscordWebhookUrl = "https://www.your-discord-webhook-url.com",
                sendPollResultToDiscord = false,
                automaticPollEnd = false,
                DefaultPollClosingTime = 3600,
                forceGlobalVoteScreenOnPollCreate = false,
                sendReminder = false,
                sendReminderTime = 900,
                timeTrackerInterval = 120,
                messagePrefix = "<color=orange>[SERVER POLL]: </color>",
                messageSuffix = ""


            };

            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<PluginConfig>();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);



        #endregion

        #region Data
        private void LoadPollData()
        {
            var data = Interface.Oxide.DataFileSystem.ReadObject<PollData>("EasyServerPollData");




            // If there is an active poll in data
            if (data != null)
            {
                this.pollActive = data.pollActive;
                this.pollQuestion = data.pollQuestion;
                this.pollOptions = data.pollOptions ?? new Dictionary<string, int>();
                this.playerVotes = data.playerVotes ?? new Dictionary<ulong, string>();
                this.remainingSeconds = data.remainingSeconds;

              

                if (pollActive)
                {
                    if (config.sendReminder)
                    {
                        reminderTimer = timer.Once(config.sendReminderTime, ReminderForVotes);
                    }

                    if (config.automaticPollEnd)
                    {
                        timeTracker = timer.Once(timeTrackerInterval, ProcessTime);
                    }
            

                    
                }


            }
            else
            {
                Puts("No active poll data found.");
                this.pollOptions = new Dictionary<string, int>();
                this.playerVotes = new Dictionary<ulong, string>();
            }
        }

        private void ProcessTime()
        {
            remainingSeconds -= timeTrackerInterval;
            timeTracker = timer.Once(timeTrackerInterval, ProcessTime);

            if (remainingSeconds < 0)
            {
                timeTracker?.Destroy();
                EndPollAndSendResults();
            }

            SavePollData();
        }

        private void ClearPollData()
        {
            pollOptions.Clear();
            playerVotes.Clear();
            timeTracker?.Destroy();
            reminderTimer?.Destroy();
            pollQuestion = null;
            pollActive = false;
            SavePollData();
        }

        private void SavePollData()
        {
            var data = new PollData
            {
                pollActive = this.pollActive,
                pollQuestion = this.pollQuestion,
                pollOptions = this.pollOptions,
                playerVotes = this.playerVotes,
                remainingSeconds = this.remainingSeconds
                
            };

            Interface.Oxide.DataFileSystem.WriteObject("EasyServerPollData", data);
        }

        #endregion

        private void ReminderForVotes()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!playerVotes.ContainsKey(player.userID))
                {
                    SendMessageToPlayer(player, "Don't forget to vote in the active poll! Use /poll to participate.");
                }
            }

            // Schedule the next reminder


            reminderTimer = timer.Once(config.sendReminderTime, ReminderForVotes);
        }


        #region ChatCommands

        [ChatCommand("startpoll")]
        private void StartPollCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permissionPollCreate))
            {
                SendMessageToPlayer(player, "You don't have permission to create a poll.");
                return;
            }

            if (args.Length < 2 || args.Length > 6)
            {
                SendMessageToPlayer(player, "Usage:\n/startpoll <question> <option1> <option2> <option3>\n... (up to 5)");
                return;
            }

            pollQuestion = args[0];
            for (int i = 1; i < args.Length; i++)
            {
                pollOptions[args[i]] = 0;
            }

            pollActive = true;

            if (config.automaticPollEnd)
            {
                remainingSeconds = config.DefaultPollClosingTime;
            }

            PrintToChat($"{config.messagePrefix}A new poll has started! Question: {pollQuestion}. Use /vote <option> to cast your vote.{config.messageSuffix}");

           
            if (config.forceGlobalVoteScreenOnPollCreate)
            {
                foreach (var activePlayer in BasePlayer.activePlayerList)
                {
                    SendPollCui(activePlayer);
                }
            }


            pollTimer?.Destroy();
            reminderTimer?.Destroy();

            reminderTimer = timer.Once(config.sendReminderTime, ReminderForVotes);

            if (config.automaticPollEnd)
            {
                timeTracker = timer.Once(timeTrackerInterval, ProcessTime);
            }

            SavePollData();


        }

        [ChatCommand("pollresults")]
        private void PollResultsCmd(BasePlayer player)
        {
            // Check if the player is an admin
            if (!permission.UserHasPermission(player.UserIDString, permissionShowResults))
            {
                SendMessageToPlayer(player, "You don't have permission to view poll results.");
                return;
            }

            // If no poll is active
            if (pollQuestion == null || pollOptions == null)
            {
     
                SendMessageToPlayer(player, "There is no active poll at the moment.");

                return;
            }

            // Send the poll question and results
            StringBuilder results = new StringBuilder();
            results.AppendLine($"Poll Question: {pollQuestion}");

            foreach (var option in pollOptions)
            {
                results.AppendLine($"{option.Key}: {option.Value} votes");
            }

            SendReply(player, results.ToString());
        }

        [ChatCommand("poll")]
        private void OpenPollCommand(BasePlayer player)
        {
            if (string.IsNullOrEmpty(pollQuestion))
            {
                SendMessageToPlayer(player, "There is no active poll at the moment.");
                return;
            }

            SendPollCui(player);
        }

        [ChatCommand("pollhistory")]
        private void ShowPollHistory(BasePlayer player, string command, string[] args)
        {
            

            if (!permission.UserHasPermission(player.UserIDString, permissionShowResults))
            {
                SendMessageToPlayer(player, "You don't have permission to view the poll history.");
                return;
            }

            if (args.Length == 0)
            {
                SendReply(player, "Usage:\n" +
                    " /pollhistory list (returns list of polls\n" +
                    "/pollhistory show <id> (returns the results of that poll." +
                    "/pollhistory clear (clears the poll history");
                
                return;
            }

            if (args[0].Contains("list"))
            {
                var history = Interface.Oxide.DataFileSystem.ReadObject<PollHistoryData>("EasyServerPollHistory");

                if (history.pollHistory == null)
                {
                    SendMessageToPlayer(player, "There is no poll history.");
                }
                else if (history.pollHistory.Count == 0)
                {
                    SendMessageToPlayer(player, "There is no poll history.");
                }
                else
                {
                    string sendString = string.Empty;
                    foreach (KeyValuePair<int, PollHistory> pair in history.pollHistory)
                    {
                        sendString += $"\n{pair.Key}: {pair.Value.pollQuestion.ToString()}";
                    }

                    sendString += "\n\nType /pollhistory show <id> to view the results of that poll.";
                    SendMessageToPlayer(player, sendString);
                    

                }
            }

            if (args[0].Contains("show") && args[1] != null)
            {
                int selectedId;

                
                bool isNumeric = int.TryParse(args[1].ToString(), out selectedId);
                if (!isNumeric)
                {
                    SendMessageToPlayer(player, "The ID you've sent does not seem to be a number.");
                    return;
                }
                else
                {
                    var history = Interface.Oxide.DataFileSystem.ReadObject<PollHistoryData>("EasyServerPollHistory");


                    if (history.pollHistory.ContainsKey(selectedId))
                    {
         
                        string options = string.Empty;

                        foreach (KeyValuePair<string,int> valuePair in history.pollHistory[selectedId].pollOptions)
                        {
                            options += $"\n{valuePair.Key}:{valuePair.Value}";
                        }

                        SendMessageToPlayer(player, $"\n=== POLL HISTORY ===\n" +
                            $"Question:\n{history.pollHistory[selectedId].pollQuestion}\n\n{options}");

                    }
                    else
                    {
                        SendMessageToPlayer(player, "This poll does not exist.");
                    }
                }
            }

        }


        [ChatCommand("endpoll")]
        private void EndPollCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permissionPollEnd))
            {
                SendMessageToPlayer(player, "You don't have permission to end a poll.");
                return;
            }

            if (!pollActive)
            {
                SendMessageToPlayer(player, "No active poll to end.");
                return;
            }

            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(activePlayer, "poll_panel");
            }

            EndPollAndSendResults();
        }

        #endregion



        #region ConsoleCommands

        [ConsoleCommand("closepoll")]
        private void ClosePollCmd(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            CuiHelper.DestroyUi(player, "poll_panel");
        }

        [ConsoleCommand("vote")]
        private void VoteCmd(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            string combinedArguments = string.Empty;

            foreach (string argument in arg.Args)
            {
                combinedArguments += argument + " ";
            }

            string fixedArguments = combinedArguments.Remove(combinedArguments.Length - 1);

            foreach (KeyValuePair<string, int> pair in pollOptions)
            {
                Puts("Option: " + pair.Key.Length);
            }

            string votedOption = arg.GetString(0, "none");
            if (votedOption == "none")
                return;

            // Check if the player has already voted
            if (playerVotes.ContainsKey(player.userID))
            {
                SendMessageToPlayer(player, "You have already voted on this poll.");
                return;
            }

            // Register vote
            if (pollOptions.ContainsKey(fixedArguments))
            {
                pollOptions[fixedArguments]++;
                playerVotes[player.userID] = fixedArguments;

                // Close the poll CUI for the player
                CuiHelper.DestroyUi(player, "poll_panel");

                // Confirm the vote to the player
                SendMessageToPlayer(player, $"Thanks for voting!\nYou have voted for:\n{fixedArguments}");

            }
            else
            {
                SendMessageToPlayer(player, "Your vote option was invalid.\nTry again. If the issue persists\nplease contact server admin.");

            }

            SavePollData();
        }


        #endregion


        #region CUI

        private void SendPollCui(BasePlayer player)
        {

            var cuiElementContainer = new CuiElementContainer();

            string panelName = cuiElementContainer.Add(
                new CuiPanel
                {
                    Image = { Color = "0.1 0.1 0.1 1.0" },
                    RectTransform = { AnchorMin = "0.3 0.3", AnchorMax = "0.691 0.717" },
                    CursorEnabled = true
                },
                "Overlay",
                "poll_panel"
            );

            // Top image
            // 0, 0.831
            // 1.002, 1
            cuiElementContainer.Add(
                new CuiPanel
                {
                    Image = { Color = "1 1 1 0.5" },
                    RectTransform = { AnchorMin = "0 0.899", AnchorMax = "1 1" },
                    CursorEnabled = true


                },
                "poll_panel"
            );


            //0.393, 0.922AnchorMax:
            //0.621, 0.976

            cuiElementContainer.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = "SERVER POLL",
                        FontSize = 14,
                        Align = TextAnchor.UpperCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform = { AnchorMin = "0.393 0.922", AnchorMax = "0.621 0.976" }
                },
                "poll_panel"
            );

            cuiElementContainer.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = pollQuestion,
                        FontSize = 14,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform = { AnchorMin = "0.1 0.8", AnchorMax = "0.9 0.9" }
                },
                "poll_panel"
            );

            int optionIndex = 0;
            foreach (var option in pollOptions.Keys)
            {
                float yMin = 0.7f - optionIndex * 0.1f;
                float yMax = yMin + 0.09f;

                cuiElementContainer.Add(
                    new CuiButton
                    {
                        Text =
                        {
                            Text = option,
                            FontSize = 12,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1"
                        },
                        Button = { Command = $"vote {option}", Color = "0.2 0.2 0.2 0.9" },
                        RectTransform = { AnchorMin = $"0.1 {yMin}", AnchorMax = $"0.9 {yMax}" }
                    },
                    "poll_panel"
                );

                optionIndex++;
            }

            // Adding the close button
            cuiElementContainer.Add(
                new CuiButton
                {
                    Text =
                    {
                        Text = "Close",
                        FontSize = 8,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    Button = { Command = "closepoll", Color = "0.7 0.2 0.2 0.7" },
                    RectTransform = { AnchorMin = "0.8 0.05", AnchorMax = "0.95 0.15" }
                },
                "poll_panel"
            );

            CuiHelper.AddUi(player, cuiElementContainer);
        }


        #endregion



        #region HelperFunctions

        private void EndPollAndSendResults()
        {
            SavePollData();
            // Compile the poll results
            StringBuilder results = new StringBuilder();
            results.AppendLine($"Poll Question: {pollQuestion}");

            foreach (var option in pollOptions)
            {
                results.AppendLine($"{option.Key}: {option.Value} votes");
            }

            if (config.sendPollResultToDiscord)
            {
                // Send results to Discord if enabled in config
                SendToDiscord(results.ToString());
            }


            var result = $"Poll results for question: {pollQuestion}\n";
            foreach (var option in pollOptions)
            {
                result += $"{option.Key}: {option.Value} votes\n";
            }

            // Create history entry
            var history = Interface.Oxide.DataFileSystem.ReadObject<PollHistoryData>("EasyServerPollHistory");

            if (history.pollHistory == null)
            {
                history.pollHistory = new Dictionary<int, PollHistory>();
                PollHistory historyEntry = new PollHistory();
                historyEntry.pollQuestion = pollQuestion;
                historyEntry.pollOptions = pollOptions;
                history.pollHistory.Add(1, historyEntry);

                Interface.Oxide.DataFileSystem.WriteObject<PollHistoryData>("EasyServerPollHistory", history);

            }
            else
            {
                int lastKey = 0;

                foreach (KeyValuePair<int, PollHistory> pair in history.pollHistory)
                {
                    lastKey = pair.Key;    
                }

                PollHistory historyEntry = new PollHistory();
                historyEntry.pollQuestion = pollQuestion;
                historyEntry.pollOptions = pollOptions;
                history.pollHistory.Add(lastKey + 1, historyEntry);

                Interface.Oxide.DataFileSystem.WriteObject<PollHistoryData>("EasyServerPollHistory", history);


            }







            PrintToChat(result);

            // Reset poll state if needed
            pollQuestion = null;
            pollOptions.Clear();
            playerVotes.Clear();
            pollActive = false;
            pollTimer?.Destroy();
            reminderTimer?.Destroy();

        }


        private void SendMessageToPlayer(BasePlayer player, string message)
        {
            string completeMessage = string.Format("{0} {1} {2}", config.messagePrefix, message, config.messageSuffix);
            SendReply(player, completeMessage);

        }
        private void SendToDiscord(string message)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { content = message });

            webrequest.EnqueuePost(
                DiscordWebhookUrl,
                json,
                (code, response) =>
                {
                    if (code != 200)
                    {
                        Puts($"Error sending results to Discord: {response}");
                    }
                    else
                    {
                        Puts("Poll results sent to Discord successfully.");
                    }
                },
                this,
                new Dictionary<string, string> { { "Content-Type", "application/json" } }
            );
        }





        #endregion



    }
}
