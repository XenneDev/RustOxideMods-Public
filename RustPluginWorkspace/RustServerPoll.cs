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
    [Info("RustServerPoll", "Xenne", "1.0.3")]
    public class RustServerPoll : RustPlugin
    {
        private class PluginConfig
        {
            public bool sendPollResultToDiscord { get; set; }
            public string DiscordWebhookUrl { get; set; }
            public int DefaultPollClosingTime { get; set; } // time in seconds
            public bool forceGlobalVoteScreenOnPollCreate { get; set; }
            public bool automaticPollEnd { get; set; }
            public bool sendReminder { get; set; }
            public int sendReminderTime { get; set; }
        }

        private class PollData
        {
            public bool pollActive;
            public string pollQuestion;
            public Dictionary<string, int> pollOptions;
            public Dictionary<ulong, string> playerVotes;
        }

        private PluginConfig config;
        private Dictionary<ulong, string> playerVotes = new Dictionary<ulong, string>();
        private Dictionary<string, int> pollOptions = new Dictionary<string, int>();

        private string pollQuestion = null;
        private bool pollActive = false;
        private Timer pollTimer;
        private Timer reminderTimer;
        private string DiscordWebhookUrl => config.DiscordWebhookUrl;
        private const string permissionPollCreate = "rustserverpoll.create";
        private const string permissionPollEnd = "rustserverpoll.endpoll";
        private const string permissionVote = "rustserverpoll.vote";


        #region Server

        private void Init()
        {
            // Load poll data
            LoadPollData();

            // Register permissions
            permission.RegisterPermission(permissionPollCreate, this);
            permission.RegisterPermission(permissionVote, this);
            permission.RegisterPermission(permissionPollEnd, this);
        }


        void Unload()
        {
            pollTimer?.Destroy();
            reminderTimer?.Destroy();

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
                sendReminderTime = 900


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
            var data = Interface.Oxide.DataFileSystem.ReadObject<PollData>("PollData");

            // If there is an active poll in data
            if (data != null)
            {
                this.pollActive = data.pollActive;
                this.pollQuestion = data.pollQuestion;
                this.pollOptions = data.pollOptions ?? new Dictionary<string, int>();
                this.playerVotes = data.playerVotes ?? new Dictionary<ulong, string>();

                reminderTimer = timer.Once(config.sendReminderTime, ReminderForVotes);

                if (config.automaticPollEnd)
                {
                    pollTimer = timer.Once(config.DefaultPollClosingTime, EndPollAndSendResults);
                }


            }
            else
            {
                this.pollOptions = new Dictionary<string, int>();
                this.playerVotes = new Dictionary<ulong, string>();
            }
        }

        private void ClearPollData()
        {
            pollOptions.Clear();
            playerVotes.Clear();
            pollTimer?.Destroy(); // Destroy any existing timer first
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
                playerVotes = this.playerVotes
            };

            Interface.Oxide.DataFileSystem.WriteObject("PollData", data);
        }

        #endregion

        private void ReminderForVotes()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!playerVotes.ContainsKey(player.userID))
                {
                    SendReply(
                        player,
                        "Don't forget to vote in the active poll! Use /poll to participate."
                    );
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
                SendReply(player, "You don't have permission to create a poll.");
                return;
            }

            if (args.Length < 2 || args.Length > 6)
            {
                SendReply(
                    player,
                    "Usage: /startpoll <question> <option1> <option2> ... (up to 5 options)"
                );
                return;
            }

            pollQuestion = args[0];
            for (int i = 1; i < args.Length; i++)
            {
                pollOptions[args[i]] = 0;
            }

            pollActive = true;

            PrintToChat(
                $"A new poll has started! Question: {pollQuestion}. Use /vote <option> to cast your vote."
            );

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
                pollTimer = timer.Once(config.DefaultPollClosingTime, EndPollAndSendResults);
            }

            SavePollData();


        }

        [ChatCommand("pollresults")]
        private void PollResultsCmd(BasePlayer player)
        {
            // Check if the player is an admin
            if (!player.IsAdmin)
            {
                SendReply(player, "You don't have permission to view poll results.");
                return;
            }

            // If no poll is active
            if (pollQuestion == null || pollOptions == null)
            {
                SendReply(player, "No active poll.");
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
                SendReply(player, "There's currently no active poll.");
                return;
            }
            SendPollCui(player);
        }

        [ChatCommand("endpoll")]
        private void EndPollCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permissionPollEnd))
            {
                SendReply(player, "You don't have permission to end a poll.");
                return;
            }

            if (!pollActive)
            {
                SendReply(player, "No active poll to end.");
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
            Puts("The console vote command args are: " + arg.GetString(0));


            var player = arg.Player();
            if (player == null)
                return;

            string combinedArguments = string.Empty;

            foreach (string argument in arg.Args)
            {
                combinedArguments += argument + " ";
            }

            string fixedArguments = combinedArguments.Remove(combinedArguments.Length - 1);


            Puts("Combined arguments: " + combinedArguments.Length);

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
                SendReply(player, "You have already voted on this poll.");
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
                SendReply(player, $"You successfully voted for: {fixedArguments}");
            }
            else
            {
                SendReply(player, "The option you voted for is not valid.");
            }

            SavePollData();
        }


        #endregion


        #region CUI

        private void SendPollCui(BasePlayer player)
        {
            Puts($"Sending Poll Question: {pollQuestion}");
            foreach (var option in pollOptions.Keys)
            {
                Puts($"Sending Poll Option: {option}");
            }

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
                // Send results to Discord
                SendToDiscord(results.ToString());
            }


            var result = $"Poll results for question: {pollQuestion}\n";
            foreach (var option in pollOptions)
            {
                result += $"{option.Key}: {option.Value} votes\n";
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
