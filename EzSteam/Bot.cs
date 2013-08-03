﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using SteamKit2;
using SteamKit2.Internal;

namespace EzSteam
{
    public enum BotDisconnectReason
    {
        ConnectFailed,
        Disconnected,

        WrongPassword,
        SteamGuard,
        LoginFailed,

        LoggedOff
    }

    public class Bot
    {
        public delegate void ConnectedEvent(Bot bot);
        public delegate void DisconnectedEvent(Bot bot, BotDisconnectReason reason);
        public delegate void PrivateEnterEvent(Bot bot, Chat chat);
        public delegate void ChatInviteEvent(Bot bot, Persona sender, SteamID chatId);
        public delegate void FriendRequestEvent(Bot bot, Persona sender);

        /// <summary>
        /// Create a new Steam Bot. Username and password must be fore the Steam account you
        /// want it to use. Steam Guard may need to be disabled for login to work.
        /// </summary>
        public Bot(string username, string password)
        {
            this.username = username;
            this.password = password;
        }

        /// <summary>
        /// Fired when the Bot successfully connects and logs in to Steam.
        /// </summary>
        public event ConnectedEvent OnConnected;

        /// <summary>
        /// Fired when connection to Steam is lost or fails.
        /// </summary>
        public event DisconnectedEvent OnDisconnected;

        /// <summary>
        /// Fired when a new private chat must be initialized.
        /// </summary>
        public event PrivateEnterEvent OnPrivateEnter;

        /// <summary>
        /// Fired when somebody invites the bot to a chat.
        /// </summary>
        public event ChatInviteEvent OnChatInvite;

        /// <summary>
        /// Fired when somebody adds the bot to its friend list.
        /// </summary>
        public event FriendRequestEvent OnFriendRequest;

        /// <summary>
        /// Gets the SteamID of the logged in Steam account.
        /// </summary>
        public SteamID PersonaId
        {
            get { return SteamUser.SteamID; }
        }

        /// <summary>
        /// Gets or sets the persona state of the Steam account.
        /// </summary>
        public EPersonaState PersonaState
        {
            get { return SteamFriends.GetPersonaState(); }
            set { SteamFriends.SetPersonaState(value); }
        }

        /// <summary>
        /// Gets or sets the persona name of the Steam account.
        /// </summary>
        public string PersonaName
        {
            get { return SteamFriends.GetPersonaName(); }
            set { SteamFriends.SetPersonaName(value); }
        }

        /// <summary>
        /// Gets the friend list of the Steam account.
        /// </summary>
        public IEnumerable<SteamID> Friends
        {
            get
            {
                for (var i = 0; i < SteamFriends.GetFriendCount(); i++)
                {
                    yield return SteamFriends.GetFriendByIndex(i);
                }
            }
        }

        /// <summary>
        /// Gets the group list of the Steam account.
        /// </summary>
        public IEnumerable<SteamID> Groups
        {
            get
            {
                for (var i = 0; i < SteamFriends.GetClanCount(); i++)
                {
                    var c = SteamFriends.GetClanByIndex(i);
                    if (SteamFriends.GetClanRelationship(c) == EClanRelationship.Member)
                        yield return c;
                }
            }
        }

        /// <summary>
        /// Gets a list of chats the bot is currently in.
        /// </summary>
        public IEnumerable<Chat> Chats
        {
            get { return chats.ToList(); }
        } 

        /// <summary>
        /// Gets or sets a game the bot should be playing. Only supports non-Steam games.
        /// </summary>
        public string Playing
        {
            get { return playing; }
            set
            {
                playing = value;

                if (string.IsNullOrEmpty(playing))
                {
                    var msg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
                    msg.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = 0, game_extra_info = "" });
                    SteamClient.Send(msg);
                }
                else
                {
                    var msg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
                    msg.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = 9759487592989982720, game_extra_info = playing });
                    SteamClient.Send(msg);
                }
            }
        }

        /// <summary>
        /// Connect and login to Steam.
        /// </summary>
        public void Connect()
        {
            if (SteamClient != null)
                throw new Exception("Connect was already called");

            SteamClient = new SteamClient();
            SteamUser = SteamClient.GetHandler<SteamUser>();
            SteamFriends = SteamClient.GetHandler<SteamFriends>();
            SteamClans = new SteamClans();
            SteamClient.AddHandler(SteamClans);

            SteamClient.Connect();

            chats = new List<Chat>();
            running = true;
            updateThread = new Thread(Run);
            updateThread.Start();
        }

        /// <summary>
        /// Disconnect from Steam. Will fire OnDisconnected.
        /// </summary>
        public void Disconnect(BotDisconnectReason reason = BotDisconnectReason.Disconnected)
        {
            foreach (var chat in chats)
            {
                chat.Leave(ChatLeaveReason.Disconnected);
            }
            chats.Clear();

            SteamClient.Disconnect();
            running = false;

            if (OnDisconnected != null)
                OnDisconnected(this, reason);
        }

        /// <summary>
        /// Attempt to join a chat. Will fire OnLeave if the chat could not be joined.
        /// </summary>
        public Chat Join(SteamID roomId)
        {
            if (roomId.IsClanAccount)
            {
                SteamFriends.RequestFriendInfo(roomId, EClientPersonaStateFlag.ClanInfo | EClientPersonaStateFlag.ClanTag | EClientPersonaStateFlag.PlayerName);
                roomId = Util.ChatFromClan(roomId);
            }

            if (chats.Any(c => c.Id == roomId))
                return chats.Find(c => c.Id == roomId);

            var chat = new Chat(this, roomId);
            SteamFriends.JoinChat(roomId);
            chats.Add(chat);

            return chat;
        }

        /// <summary>
        /// Gets the persona of another Steam user. Will return null if the given ID is
        /// not valid. Information may not be instantly available if the user was not 
        /// "seen" before.
        /// </summary>
        public Persona GetPersona(SteamID id)
        {
            return id.IsIndividualAccount ? new Persona(this, id) : null;
        }

        /// <summary>
        /// Adds somebody to the bot's friend list.
        /// </summary>
        public void AddFriend(SteamID id)
        {
            SteamFriends.AddFriend(id);
        }

        /// <summary>
        /// Removes somebody from the bot's friend list.
        /// </summary>
        public void RemoveFriend(SteamID id)
        {
            SteamFriends.RemoveFriend(id);
        }

        /// <summary>
        /// Provides access to the internal SteamKit SteamClient instance.
        /// </summary>
        public SteamClient SteamClient;

        /// <summary>
        /// Provides access to the internal SteamKit SteamUser instance.
        /// </summary>
        public SteamUser SteamUser;

        /// <summary>
        /// Provides access to the internal SteamKit SteamFriends instance.
        /// </summary>
        public SteamFriends SteamFriends;

        private readonly string username;
        private readonly string password;
        internal SteamClans SteamClans;

        private List<Chat> chats;

        private Thread updateThread;
        private bool running;

        private string playing;

        private void Run()
        {
            while (running)
            {
                var msg = SteamClient.WaitForCallback(true);

                msg.Handle<SteamClient.ConnectedCallback>(callback =>
                {
                    if (callback.Result != EResult.OK)
                    {
                        Disconnect(BotDisconnectReason.ConnectFailed);
                        running = false;
                        return;
                    }

                    SteamUser.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = username,
                        Password = password
                    });
                });

                msg.Handle<SteamClient.DisconnectedCallback>(callback =>
                {
                    Disconnect();
                    running = false;
                });

                msg.Handle<SteamUser.LoggedOnCallback>(callback =>
                {
                    if (callback.Result == EResult.OK)
                        return;

                    var res = BotDisconnectReason.LoginFailed;

                    if (callback.Result == EResult.AccountLogonDenied)
                        res = BotDisconnectReason.SteamGuard;

                    if (callback.Result == EResult.InvalidPassword)
                        res = BotDisconnectReason.WrongPassword;

                    Disconnect(res);
                    running = false;
                });

                msg.Handle<SteamUser.LoginKeyCallback>(callback =>
                {
                    if (OnConnected != null)
                        OnConnected(this);
                });

                msg.Handle<SteamUser.LoggedOffCallback>(callback =>
                {
                    Disconnect(BotDisconnectReason.LoggedOff);

                    running = false;
                });

                msg.Handle<SteamFriends.FriendMsgCallback>(callback =>
                {
                    if (callback.EntryType != EChatEntryType.ChatMsg) return;

                    if (chats.Count(c => c.Id == callback.Sender) == 0)
                    {
                        var c = Join(callback.Sender);

                        if (OnPrivateEnter != null)
                            OnPrivateEnter(this, c);
                    }
                });

                msg.Handle<SteamFriends.FriendsListCallback>(callback =>
                {
                    foreach (var friend in callback.FriendList)
                    {
                        var f = friend;
                        if (friend.Relationship == EFriendRelationship.RequestRecipient && OnFriendRequest != null)
                            OnFriendRequest(this, new Persona(this, f.SteamID));
                    }
                });

                msg.Handle<SteamFriends.ChatInviteCallback>(callback =>
                {
                    if (OnChatInvite != null)
                        OnChatInvite(this, new Persona(this, callback.PatronID), callback.ChatRoomID);
                });

                foreach (var c in chats.ToList())
                {
                    c.Handle(msg);
                }

                chats.RemoveAll(c => !c.IsActive);
            }
        }
    }
}
