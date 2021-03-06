﻿using System;
using System.CodeDom;
using Disa.Framework.Bubbles;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SharpTelegram;
using SharpMTProto;
using SharpMTProto.Transport;
using SharpMTProto.Authentication;
using SharpTelegram.Schema;
using System.Linq;
using SharpMTProto.Messaging.Handlers;
using SharpMTProto.Schema;
using System.Globalization;
using System.Timers;
using System.IO;
using System.Reactive;
using System.Security.Cryptography;
using System.Drawing;
using ProtoBuf;
using IMessage = SharpTelegram.Schema.IMessage;
using Message = SharpTelegram.Schema.Message;
using static Disa.Framework.Bubbles.Bubble;
using Disa.Framework.Bots;

//TODO:
//1) After authorization, there's an expiry time. Ensure that the login expires by then (also, in DC manager)

namespace Disa.Framework.Telegram
{
    [ServiceInfo(
        serviceName: "Telegram", 
        eventDrivenBubbles: true, 
        usesMediaProgress: true, 
        usesInternet: true, 
        supportsBatterySavingsMode: false, 
        delayedNotifications: true, 
        sendingQuotes: true, 
        settings: typeof(TelegramSettings),
        procedureType: ServiceInfo.ProcedureType.ConnectAuthenticate, 
        supportedBubbles: new Type[] {
        typeof(AudioBubble),
        typeof(ContactBubble),
        typeof(FileBubble),
        typeof(ImageBubble),
        typeof(LocationBubble),
        typeof(PresenceBubble),
        typeof(ReadBubble),
        typeof(StickerBubble),
        typeof(TextBubble),
        typeof(TypingBubble), })]
    [AudioParameters(
        recordType: AudioParameters.RecordType.M4A, 
        durationLimit: AudioParameters.NoDurationLimit, 
        sizeLimit: 25000000, // 25MB
        supportedExtensions: new string[]
            { ".mp3", ".aac", ".m4a", ".mp4", ".wav", ".3ga", ".3gp", ".3gpp", ".amr", ".ogg", ".webm", ".weba", ".opus" })]
    [FileParameters(
        sizeLimit: 1000000000)] //1GB
    [GifParameters(
        gifRecordType: GifParameters.RecordType.Gif,
        sizeLimit: 25000000, // 25MB
        supportedExtensions: new string[] { ".gif" })]
    [StickerParameters(
        stickerRecordType: StickerParameters.RecordType.Webp,
        sizeLimit: 25000000, // 25MB
        supportedExtensions: new string[] { ".gif", ".webp" })]
    public partial class Telegram : Service, IVisualBubbleServiceId, ITerminal
    {
        public static uint MESSAGE_FLAG_REPLY = 0x00000001;
        public static uint MESSAGE_FLAG_ENTITIES = 0x00000008;
        public static uint MESSAGE_FLAG_HAS_MARKUP = 0x00000040;

        private object _cachedThumbnailsLock = new object();

        private static TcpClientTransportConfig DefaultTransportConfig = 
            new TcpClientTransportConfig("149.154.167.50", 443);

        private readonly object _baseMessageIdCounterLock = new object();
        private string _baseMessageId = "0000000000";
        private int _baseMessageIdCounter;

        private List<User> contactsCache = new List<User>();

        public bool LoadConversations;

        private object _quickUserLock = new object();

        private TelegramClient cachedClient;

        private bool _oldMessages = false;

		private object _globalBubbleLock = new object();
        //msgid, address, date
        private Dictionary<uint,Tuple<uint,uint,bool>> messagesUnreadCache = new Dictionary<uint, Tuple<uint, uint,bool>>();

        public string CurrentMessageId
        {
            get
            {
                return _baseMessageId + Convert.ToString(_baseMessageIdCounter);
            }
        }

        public string NextMessageId
        {
            get
            {
                lock (_baseMessageIdCounterLock)
                {
                    _baseMessageIdCounter++;
                    return CurrentMessageId;
                }
            }
        }

        public Config Config
        {
            get 
            {
                return _config;
            }
        }

        public TelegramSettings Settings
        {
            get
            {
                return _settings;   
            }
        }

        public CachedDialogs Dialogs
        {
            get
            {
                return _dialogs;
            }
        }

        private bool _hasPresence;

        private bool _longPollerAborted;

        private Random _random = new Random(System.Guid.NewGuid().GetHashCode());

        private TelegramSettings _settings;

        private TelegramMutableSettings _mutableSettings;

        private TelegramClient _longPollClient;

        private readonly object _mutableSettingsLock = new object();
        
        private CachedDialogs _dialogs = new CachedDialogs();

        private Config _config;

        private Dictionary<string, Timer> _typingTimers = new Dictionary<string, Timer>();

        private ConcurrentDictionary<string, bool> _thumbnailDownloadingDictionary = new ConcurrentDictionary<string, bool>();

		private ConcurrentDictionary<string, DisaThumbnail> _thumbnailCache = new ConcurrentDictionary<string, DisaThumbnail>();

        private WakeLockBalancer.GracefulWakeLock _longPollHeartbeart;

        private string _thumbnailDatabasePathCached;

        private void CancelTypingTimer(string id)
        {
            if (_typingTimers.ContainsKey(id))
            {
                var timer = _typingTimers[id];
                timer.Stop();
                timer.Dispose();
            }
        }

        public void SaveState(uint date, uint pts, uint qts, uint seq)
        {
            lock (_mutableSettingsLock)
            {
                if (date != 0)
                {
                    _mutableSettings.Date = date;
                }
                if (pts != 0)
                {
                    _mutableSettings.Pts = pts;
                }
                if (qts != 0)
                {
                    _mutableSettings.Qts = qts;
                }
                if (seq != 0)
                {
                    _mutableSettings.Seq = seq;
                }
                MutableSettingsManager.Save(_mutableSettings);
            }
        }

        private object NormalizeUpdateIfNeeded(object obj)
        {
            // flatten UpdateNewMessage to Message
            var newMessage = obj as UpdateNewMessage;
            var newChannelMessage = obj as UpdateNewChannelMessage;
            if (newMessage != null)
            {
                return newMessage.Message;
            }
            if (newChannelMessage != null)
            {
                var message = newChannelMessage.Message as Message;
                if (message != null)
                {
                    var peerChannel = message.ToId as PeerChannel;
                    uint channelId = peerChannel.ChannelId;
                    SaveChannelState(channelId, newChannelMessage.Pts); 
                }
                return newChannelMessage.Message;
            }
            return obj;
        }

        private void SaveChannelState(uint channelId, uint pts)
        {
            if (pts == 0)
                return;
            _dialogs.UpdateChatPts(channelId, pts);
        }

        private List<object> AdjustUpdates(List<object> updates)
        {
            if (updates == null)
            {
                return new List<object>();
            }
            var precedents = new List<object>();
            var successors = updates.ToList();
            foreach (var update in successors.ToList())
            {
                var user = update as IUser;
                var chat = update as IChat;
                if (user != null || chat != null)
                {
                    precedents.Add(update);
                    successors.Remove(update);
                }
            }
            return precedents.Concat(successors).ToList();
        }

        public override Task GetQuotedMessageTitle(VisualBubble bubble, Action<string> result)
        {
            return Task.Factory.StartNew(() =>
            {
                var userId = bubble.QuotedAddress;
                if (userId != null)
                {
                    uint userIdInt = uint.Parse(userId);
                    string username = TelegramUtils.GetUserName(_dialogs.GetUser(userIdInt));
                    result(username);
                }
            });
        }

		public void TelegramEventBubble(Bubble bubble)
		{ 
			lock(_globalBubbleLock)
			{
				EventBubble(bubble);
			}
		}

        private void ProcessIncomingPayload(List<object> payloads, bool useCurrentTime, TelegramClient optionalClient = null)
        {
            uint maxMessageId = 0;

            foreach (var payload in AdjustUpdates(payloads))
            {
                var update = NormalizeUpdateIfNeeded(payload);

                var shortMessage = update as UpdateShortMessage;
                var shortChatMessage = update as UpdateShortChatMessage;
                var typing = update as UpdateUserTyping;
                var typingChat = update as UpdateChatUserTyping;
                var userStatus = update as UpdateUserStatus;
                var messageService = update as MessageService;
                var updateChatParticipants = update as UpdateChatParticipants;
                var updateContactRegistered = update as UpdateContactRegistered;
                var updateContactLink = update as UpdateContactLink;
                var updateUserPhoto = update as UpdateUserPhoto;
                var updateReadHistoryInbox = update as UpdateReadHistoryInbox;
                var updateReadHistoryOutbox = update as UpdateReadHistoryOutbox;
                var updateReadChannelInbox = update as UpdateReadChannelInbox;
                var updateReadChannelOutbox = update as UpdateReadChannelOutbox;
                var updateChannelTooLong = update as UpdateChannelTooLong;
                var updateDeleteChannelMessage = update as UpdateDeleteChannelMessages;
                var updateEditChannelMessage = update as UpdateEditChannelMessage;
                var updateChatAdmins = update as UpdateChatAdmins;
                var updateChannel = update as UpdateChannel;
                var updateEditMessage = update as UpdateEditMessage;
                var message = update as SharpTelegram.Schema.Message;
                var user = update as IUser;
                var chat = update as IChat;

                if (shortMessage != null)
                {
                    if (!string.IsNullOrWhiteSpace(shortMessage.Message))
                    {
                        var fromId = shortMessage.UserId.ToString(CultureInfo.InvariantCulture);
                        var shortMessageUser = _dialogs.GetUser(shortMessage.UserId);
                        if (shortMessageUser == null)
                        {
                            DebugPrint(">>>>> User is null, fetching user from the server");
                            GetMessage(shortMessage.Id, optionalClient);
                        }

                        TelegramEventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                            Bubble.BubbleDirection.Incoming,
                            fromId, false, this, false, false));
                        TextBubble textBubble = new TextBubble(
                                            useCurrentTime ? Time.GetNowUnixTimestamp() : (long)shortMessage.Date,
                                            shortMessage.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                            fromId, null, false, this, shortMessage.Message,
                                            shortMessage.Id.ToString(CultureInfo.InvariantCulture));
                        textBubble.IsServiceIdSequence = true;

                        if (shortMessage.Out != null)
                        {
                            textBubble.Status = Bubble.BubbleStatus.Sent;
							BubbleGroupManager.SetUnread(this, false, fromId);
							NotificationManager.Remove(this, fromId);
                        }

                        if (shortMessage.ReplyToMsgId != 0)
                        {
                            var iReplyMessage = GetMessage(shortMessage.ReplyToMsgId, optionalClient);
                            var replyMessage = iReplyMessage as Message;
                            AddQuotedMessageToBubble(replyMessage, textBubble);
                        }

                        // Do we have any mentions to process?
                        if (shortMessage.Entities != null)
                        {
                            textBubble.BubbleMarkups = HandleEntities(
                                message: shortMessage.Message,
                                entities: shortMessage.Entities,
                                bubbleGroupAddress: fromId,
                                extendedParty: textBubble.ExtendedParty, 
                                optionalClient: optionalClient);
                        }

                        // Was this sent via a Bot?
                        textBubble.ViaBotId = shortMessage.ViaBotId > 0 ? shortMessage.ViaBotId.ToString() : null;

                        TelegramEventBubble(textBubble);
                    }
                    if (shortMessage.Id > maxMessageId)
                    {
                        maxMessageId = shortMessage.Id;
                    }
                }
                else if (updateEditMessage != null)
                {
                    var updatedMessage = updateEditMessage.Message as Message;
                    if (updatedMessage != null)
                    {
                        // First, let's get a representation of the Bubble that's been updated
                        var updatedBubbles = ProcessFullMessage(updatedMessage, useCurrentTime, optionalClient);
                        var updatedBubble = updatedBubbles.Count >= 1 ? updatedBubbles[0] : null;
                        
                        if (updatedBubble != null)
                        {
                            // Now, let's find the original Bubble on-device we want to update
                            var originalBubble = BubbleManager.FindAll(this, updatedBubble.Address)
                                .Where(b => b.IdService == updatedBubble.IdService)
                                .FirstOrDefault();
                            if (originalBubble != null)
                            {
                                // Ok, for the updated Bubble representation, update it's identity
                                updatedBubble.ID = originalBubble.ID;

                                // Now, go for the local store update
                                var originalGroup = BubbleGroupManager.FindWithAddress(this, updatedBubble.Address);
                                if (originalGroup != null)
                                {
                                    BubbleManager.Update(originalGroup, updatedBubble);

                                    // AND, publish an event in case anyone (e.g., Inline Keyboard) wants
                                    // to update their UI for the updated Bubble
                                    BubbleGroupEvents.RaiseBubbleUpdated(updatedBubble, originalGroup);
                                }
                            }
                        }
                    }
                }
                else if (updateUserPhoto != null)
                {
                    var iUpdatedUser = _dialogs.GetUser(updateUserPhoto.UserId);
                    var updatedUser = iUpdatedUser as User;
                    if (updatedUser != null)
                    {
                        updatedUser.Photo = updateUserPhoto.Photo;
                        InvalidateThumbnail(updatedUser.Id.ToString(), false, false);
                        InvalidateThumbnail(updatedUser.Id.ToString(), false, true);
                    }
                    _dialogs.AddUser(updatedUser);
                }
                else if (updateChannel != null)
                {
                    var channel = _dialogs.GetChat(updateChannel.ChannelId) as Channel;
                    if (channel != null)
                    {
                        var bubbleGroupAddress = updateChannel.ChannelId.ToString();
                        var bubbleGroup = BubbleGroupManager.FindWithAddress(this, bubbleGroupAddress);
                        if (bubbleGroup != null)
                        {
                            if (channel.Left != null)
                            {
                                // do nothing
                            }
                            else
                            {
                                BubbleGroupEvents.RaiseInputUpdated(bubbleGroup);
                                // Ok, we haven't left this group, so is this a new bubblegroup we have just been
                                // added to that we need to kick start with a partyinformation bubble?
                                //if (bubbleGroup == null &&
                                //    channel.Creator == null)
                                //{
                                //    var partyInformationBubble = new PartyInformationBubble(
                                //        time: Time.GetNowUnixTimestamp(),
                                //        direction: BubbleDirection.Incoming,
                                //        address: bubbleGroupAddress,
                                //        participantAddress: null,
                                //        party: true,
                                //        service: this,
                                //        idService: null,
                                //        type: PartyInformationBubble.InformationType.AddedToChannel,
                                //        influencer: null,
                                //        affected: null);

                                //    TelegramEventBubble(partyInformationBubble);
                                //}
                            }
                        }
                    }
                }
                else if (updateChatAdmins != null)
                {
                    var chatToUpdate = _dialogs.GetUser(updateChatAdmins.ChatId) as Chat;
                    if (chatToUpdate != null)
                    {
                        if (updateChatAdmins.Enabled)
                        {
                            chatToUpdate.AdminsEnabled = new True();
                        }
                        else
                        {
                            chatToUpdate.AdminsEnabled = null;
                        }
                    }
                    _dialogs.AddChat(chatToUpdate);
                }
                else if (updateReadHistoryOutbox != null)
                {
                    var iPeer = updateReadHistoryOutbox.Peer;
                    var peerChat = iPeer as PeerChat;
                    var peerUser = iPeer as PeerUser;

                    if (peerUser != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                            peerUser.UserId.ToString(CultureInfo.InvariantCulture));
                        DebugPrint("Found bubble group " + bubbleGroup);
                        if (bubbleGroup != null)
                        {
                            string idString = bubbleGroup.LastBubbleSafe().IdService;
                            if (idString == updateReadHistoryOutbox.MaxId.ToString(CultureInfo.InvariantCulture))
                            {
                                TelegramEventBubble(
                                    new ReadBubble(Time.GetNowUnixTimestamp(),
                                        Bubble.BubbleDirection.Incoming, this,
                                        peerUser.UserId.ToString(CultureInfo.InvariantCulture), null,
                                        Time.GetNowUnixTimestamp(), false, false));
                            }
                        }
                    }
                    else if (peerChat != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                           peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                        if (bubbleGroup != null)
                        {
                            string idString = bubbleGroup.LastBubbleSafe().IdService;
                            if (idString == updateReadHistoryOutbox.MaxId.ToString(CultureInfo.InvariantCulture))
                            {
                                TelegramEventBubble(
                                    new ReadBubble(
                                        Time.GetNowUnixTimestamp(),
                                        Bubble.BubbleDirection.Incoming, this,
                                        peerChat.ChatId.ToString(CultureInfo.InvariantCulture), DisaReadTime.SingletonPartyParticipantAddress,
                                        Time.GetNowUnixTimestamp(), true, false));
                            }
                        }

                    }

                }
                else if (updateReadChannelOutbox != null)
                {
                    BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                          updateReadChannelOutbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                    if (bubbleGroup != null)
                    {
                        string idString = bubbleGroup.LastBubbleSafe().IdService;
                        if (idString == updateReadChannelOutbox.MaxId.ToString(CultureInfo.InvariantCulture))
                        {
                            TelegramEventBubble(
                                new ReadBubble(
                                    Time.GetNowUnixTimestamp(),
                                    Bubble.BubbleDirection.Incoming, this,
                                    updateReadChannelOutbox.ChannelId.ToString(CultureInfo.InvariantCulture), DisaReadTime.SingletonPartyParticipantAddress,
                                    Time.GetNowUnixTimestamp(), true, false));
                        }
                    }
                }
                else if (updateReadHistoryInbox != null)
                {
                    var iPeer = updateReadHistoryInbox.Peer;
                    var peerChat = iPeer as PeerChat;
                    var peerUser = iPeer as PeerUser;

                    if (peerUser != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                            peerUser.UserId.ToString(CultureInfo.InvariantCulture));

                        if (bubbleGroup == null)
                        {
                            continue;
                        }

                        string idString = bubbleGroup.LastBubbleSafe().IdService;
						if (idString != null)
						{
							if (uint.Parse(idString) <= updateReadHistoryInbox.MaxId)
							{
								BubbleGroupManager.SetUnread(this, false, peerUser.UserId.ToString(CultureInfo.InvariantCulture));
								NotificationManager.Remove(this, peerUser.UserId.ToString(CultureInfo.InvariantCulture));
							}
						}
                    }
                    else if (peerChat != null)
                    {
                        BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                            peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                        if (bubbleGroup == null)
                        {
                            continue;
                        }
                        string idString = bubbleGroup.LastBubbleSafe().IdService;
                        if (idString != null)
                        {
                            if (uint.Parse(idString) == updateReadHistoryInbox.MaxId)
                            {
                                BubbleGroupManager.SetUnread(this, false,
                                    peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                                NotificationManager.Remove(this, peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                            }
                        }

                    }

                }
                else if (updateReadChannelInbox != null)
                {
                    BubbleGroup bubbleGroup = BubbleGroupManager.FindWithAddress(this,
                                              updateReadChannelInbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                    if (bubbleGroup == null)
                    {
                        continue;
                    }

                    string idString = bubbleGroup.LastBubbleSafe().IdService;
                    if (idString != null)
                    {
                        if (uint.Parse(idString) <= updateReadChannelInbox.MaxId)
                        {
                            BubbleGroupManager.SetUnread(this, false, updateReadChannelInbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                            NotificationManager.Remove(this, updateReadChannelInbox.ChannelId.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
                else if (shortChatMessage != null)
                {
                    if (!string.IsNullOrWhiteSpace(shortChatMessage.Message))
                    {
                        var address = shortChatMessage.ChatId.ToString(CultureInfo.InvariantCulture);
                        var participantAddress = shortChatMessage.FromId.ToString(CultureInfo.InvariantCulture);
                        TelegramEventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                            Bubble.BubbleDirection.Incoming,
                            address, participantAddress, true, this, false, false));
						var shortMessageChat = _dialogs.GetChat(shortChatMessage.ChatId);
						if (shortMessageChat == null)
						{
							DebugPrint(">>>>> Chat is null, fetching user from the server");
							GetMessage(shortChatMessage.Id, optionalClient);
						}
                        TextBubble textBubble = new TextBubble(
                            useCurrentTime ? Time.GetNowUnixTimestamp() : (long)shortChatMessage.Date,
                            shortChatMessage.Out != null
                                ? Bubble.BubbleDirection.Outgoing
                                : Bubble.BubbleDirection.Incoming,
                            address, participantAddress, true, this, shortChatMessage.Message,
                            shortChatMessage.Id.ToString(CultureInfo.InvariantCulture));
                        textBubble.IsServiceIdSequence = true;
                        if (shortChatMessage.Out != null)
                        {
                            textBubble.Status = Bubble.BubbleStatus.Sent;
							BubbleGroupManager.SetUnread(this, false, address);
							NotificationManager.Remove(this, address);
                        }

                        if (shortChatMessage.ReplyToMsgId != 0)
                        {
                            var iReplyMessage = GetMessage(shortChatMessage.ReplyToMsgId, optionalClient);
                            var replyMessage = iReplyMessage as Message;
                            AddQuotedMessageToBubble(replyMessage, textBubble);
                        }

                        // Do we have any mentions to process?
                        if (shortChatMessage.Entities != null)
                        {
                            textBubble.BubbleMarkups = HandleEntities(
                                message: shortChatMessage.Message, 
                                entities: shortChatMessage.Entities, 
                                bubbleGroupAddress: address, 
                                extendedParty: textBubble.ExtendedParty, 
                                optionalClient: optionalClient);
                        }

                        // Was this sent by a bot?
                        textBubble.ViaBotId = shortChatMessage.ViaBotId > 0 ? shortChatMessage.ViaBotId.ToString() : null;

                        TelegramEventBubble(textBubble);
                    }
                    if (shortChatMessage.Id > maxMessageId)
                    {
                        maxMessageId = shortChatMessage.Id;
                    }
                }
                else if (message != null)
                {
                    var bubbles = ProcessFullMessage(message, useCurrentTime, optionalClient);
                    var i = 0;
                    foreach (var bubble in bubbles)
                    {
                        if (bubble.Direction == Bubble.BubbleDirection.Incoming)
                        {
                            var fromId = message.FromId.ToString(CultureInfo.InvariantCulture);
                            var messageUser = _dialogs.GetUser(message.FromId);
                            if (messageUser == null)
                            {
                                DebugPrint(">>>>> User is null, fetching user from the server");
                                GetMessage(message.Id, optionalClient, uint.Parse(TelegramUtils.GetPeerId(message.ToId)), message.ToId is PeerChannel);
                            }
                        }
                        else if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
                        {
                            BubbleGroupManager.SetUnread(this, false, bubble.Address);
                            NotificationManager.Remove(this, bubble.Address);
                        }

                        if (message.ReplyToMsgId != 0 && i == 0)//we should only add quoted message to first bubble if multiple bubbles exist
                        {
                            var iReplyMessage = GetMessage(message.ReplyToMsgId, optionalClient, uint.Parse(TelegramUtils.GetPeerId(message.ToId)), message.ToId is PeerChannel);
                            var replyMessage = iReplyMessage as Message;
                            AddQuotedMessageToBubble(replyMessage, bubble);
                        }

                        TelegramEventBubble(bubble);
                        i++;
                    }
                    if (message.Id > maxMessageId)
                    {
                        maxMessageId = message.Id;
                    }
                }
                else if (updateContactRegistered != null)
                {
                    contactsCache = new List<User>(); //invalidate cache
                }
                else if (updateContactLink != null)
                {
                    contactsCache = new List<User>();
                }
                else if (userStatus != null)
                {
                    var available = TelegramUtils.GetAvailable(userStatus.Status);
                    var userToUpdate = _dialogs.GetUser(userStatus.UserId);
                    if (userToUpdate != null)
                    {
                        var userToUpdateAsUser = userToUpdate as User;
                        if (userToUpdateAsUser != null)
                        {
                            userToUpdateAsUser.Status = userStatus.Status;
                            _dialogs.AddUser(userToUpdateAsUser);
                        }
                    }

                    TelegramEventBubble(new PresenceBubble(Time.GetNowUnixTimestamp(),
                        Bubble.BubbleDirection.Incoming,
                        userStatus.UserId.ToString(CultureInfo.InvariantCulture),
                        false, this, available));
                }
                else if (typing != null || typingChat != null)
                {
                    var isAudio = false;
                    var isTyping = false;
                    if (typing != null)
                    {
                        isAudio = typing.Action is SendMessageRecordAudioAction;
                        isTyping = typing.Action is SendMessageTypingAction;
                    }
                    if (typingChat != null)
                    {
                        isAudio = typingChat.Action is SendMessageRecordAudioAction;
                        isTyping = typingChat.Action is SendMessageTypingAction;
                    }
                    var userId = typing != null ? typing.UserId : typingChat.UserId;
                    var party = typingChat != null;
                    var participantAddress = party ? userId.ToString(CultureInfo.InvariantCulture) : null;
                    var address = party
                        ? typingChat.ChatId.ToString(CultureInfo.InvariantCulture)
                        : userId.ToString(CultureInfo.InvariantCulture);
                    var key = address + participantAddress;


                    if (isAudio || isTyping)
                    {
                        TelegramEventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                            Bubble.BubbleDirection.Incoming,
                            address, participantAddress, party,
                            this, true, isAudio));
                        CancelTypingTimer(key);
                        var newTimer = new Timer(6000) { AutoReset = false };
                        newTimer.Elapsed += (sender2, e2) =>
                        {
                            EventBubble(new TypingBubble(Time.GetNowUnixTimestamp(),
                                Bubble.BubbleDirection.Incoming,
                                address, participantAddress, party,
                                this, false, isAudio));
                            newTimer.Dispose();
                            _typingTimers.Remove(key);
                        };
                        _typingTimers[key] = newTimer;
                        newTimer.Start();
                    }
                    else
                    {
                        //TODO: handle unknown typing action
                    }
                }
                else if (user != null)
                {
                    _dialogs.AddUser(user);
                }
                else if (chat != null)
                {
                    _dialogs.AddChat(chat);
                }
                else if (updateChatParticipants != null)
                {
                    //do nothing, we just use party options for this
                }
                else if (messageService != null)
                {
					var partyInformationBubbles = MakePartyInformationBubble(messageService, useCurrentTime, optionalClient);
                    foreach (var partyInformationBubble in partyInformationBubbles)
                    {
                        TelegramEventBubble(partyInformationBubble);
                    }
                }
                else if (updateChannelTooLong != null)
                {
                    //this dude gives me a pts of 0, which messes up shit. So we wont do nothin mah man
                    //SaveChannelState(updateChannelTooLong.ChannelId, updateChannelTooLong.Pts);
                }
                else if (updateEditChannelMessage != null)
                {
                    var editChannelMessage = updateEditChannelMessage.Message as Message;
                    if (message == null)
                    {
                        continue;
                    }
                    var peerChannel = editChannelMessage.ToId as PeerChannel;
                    SaveChannelState(peerChannel.ChannelId, updateEditChannelMessage.Pts);
                }
                else if (updateDeleteChannelMessage != null)
                {
                    SaveChannelState(updateDeleteChannelMessage.ChannelId, updateDeleteChannelMessage.Pts);
                }
                else
                {
                    DebugPrint("Unknown update: " + ObjectDumper.Dump(update));
                }
            }

            //if (maxMessageId != 0)
            //{
            //    SendReceivedMessages(optionalClient, maxMessageId);
            //}
        }

        private readonly Dictionary<string, List<Mention>> _cachedMentions = new Dictionary<string, List<Mention>>();

        // Helper method for parsing: 
        // - Telegram collection of IMessageEntity 
        // to
        // - Disa collection of BubbleMarkup
        private List<BubbleMarkup> HandleEntities(
            string message, 
            List<IMessageEntity> entities, 
            string bubbleGroupAddress,
            bool extendedParty,
            TelegramClient optionalClient = null)
        {
            var bubbleMarkups = new List<BubbleMarkup>();
            if (entities == null)
            {
                return bubbleMarkups;
            }

            var myAddress = _settings.AccountId.ToString(CultureInfo.InvariantCulture);

            foreach (var entity in entities)
            {
                // @Bill where Bill is the username not the name (for comparison see MessageEntityMentionName below)
                if (entity is MessageEntityMention)
                {
                    string mentionParticipantAddress = null;

                    var messageEntityMention = entity as MessageEntityMention;
                    var offset = (int)messageEntityMention.Offset;
                    var length = (int)messageEntityMention.Length;
                    var username = message.Substring(offset, length);

                    var bubbleGroup = BubbleGroupManager.FindWithAddress(this, bubbleGroupAddress);
                    if (bubbleGroup != null)
                    {
                        var mentions = bubbleGroup.Mentions.ToList();
                        if (mentions != null)
                        {
                            var mention = mentions.FirstOrDefault(x => x.Value == username);
                            if (mention != null)
                            {
                                mentionParticipantAddress = mention.Address;
                            }
                        }
                    }

                    if (mentionParticipantAddress == null)
                    {
                        lock (_cachedMentions)
                        {
                            if (_cachedMentions.ContainsKey(bubbleGroupAddress))
                            {
                                var mentions = _cachedMentions[bubbleGroupAddress];
                                var mention = mentions.FirstOrDefault(x => x.Value == username);
                                if (mention != null)
                                {
                                    mentionParticipantAddress = mention.Address;
                                }
                            }
                        }
                    }

                    if (mentionParticipantAddress == null)
                    {
                        try
                        {
                            var mentions = MentionsGetMentions("@", bubbleGroupAddress, extendedParty, optionalClient);
                            lock (_cachedMentions)
                            {
                                _cachedMentions[bubbleGroupAddress] = mentions;
                            }
                            var mention = mentions.FirstOrDefault(x => x.Value == username);
                            if (mention != null)
                            {
                                mentionParticipantAddress = mention.Address;
                            }
                        }
                        catch
                        {
                            //FIXME: this usually happens because we have some backoff on an API call. Should rarely happen, but
                            //       if it does, then we'll ignore markups.
                        }
                    }

                    if (mentionParticipantAddress!= null)
                    {
                        bubbleMarkups.Add(new BubbleMarkupMentionUsername
                        {
                            Offset = offset,
                            Length = length,
                            Address = mentionParticipantAddress,
                            IsMyself = myAddress == mentionParticipantAddress
                        });
                    }
                }
                else if (entity is MessageEntityHashtag)
                {
                    var messageEntityHashtag = entity as MessageEntityHashtag;
                    bubbleMarkups.Add(new BubbleMarkupHashtag
                    {
                        Offset = (int)messageEntityHashtag.Offset,
                        Length = (int)messageEntityHashtag.Length
                    });
                }
                else if (entity is MessageEntityBotCommand)
                {
                    var messageEntityBotCommand = entity as MessageEntityBotCommand;
                    bubbleMarkups.Add(new BubbleMarkupBotCommand
                    {
                        Offset = (int)messageEntityBotCommand.Offset,
                        Length = (int)messageEntityBotCommand.Length
                    });
                }
                else if (entity is MessageEntityUrl)
                {
                    var messageEntityUrl = entity as MessageEntityUrl;
                    var offset = (int)messageEntityUrl.Offset;
                    var length = (int)messageEntityUrl.Length;
                    var bubbleMarkupUrl = new BubbleMarkupUrl
                    {
                        Offset = offset,
                        Length = length,
                    };
                    if (message != null &&
                        message.Length >= offset + length)
                    {
                        bubbleMarkupUrl.Url = message.Substring(offset, length);
                    }
                    bubbleMarkups.Add(bubbleMarkupUrl);
                }
                else if (entity is MessageEntityEmail)
                {
                    var messageEntityEmail = entity as MessageEntityEmail;
                    bubbleMarkups.Add(new BubbleMarkupEmail
                    {
                        Offset = (int)messageEntityEmail.Offset,
                        Length = (int)messageEntityEmail.Length
                    });
                }
                else if (entity is MessageEntityBold)
                {
                    var messageEntityBold = entity as MessageEntityBold;
                    bubbleMarkups.Add(new BubbleMarkupBold
                    {
                        Offset = (int)messageEntityBold.Offset,
                        Length = (int)messageEntityBold.Length
                    });
                }
                else if (entity is MessageEntityItalic)
                {
                    var messageEntityItalic = entity as MessageEntityItalic;
                    bubbleMarkups.Add(new BubbleMarkupItalic
                    {
                        Offset = (int)messageEntityItalic.Offset,
                        Length = (int)messageEntityItalic.Length
                    });
                }
                else if (entity is MessageEntityCode)
                {
                    var messageEntityCode = entity as MessageEntityCode;
                    bubbleMarkups.Add(new BubbleMarkupCode
                    {
                        Offset = (int)messageEntityCode.Offset,
                        Length = (int)messageEntityCode.Length
                    });
                }
                else if (entity is MessageEntityPre)
                {
                    var messageEntityPre = entity as MessageEntityPre;
                    bubbleMarkups.Add(new BubbleMarkupPre
                    {
                        Offset = (int)messageEntityPre.Offset,
                        Length = (int)messageEntityPre.Length,
                        Language = messageEntityPre.Language
                    });

                }
                else if (entity is MessageEntityTextUrl)
                {
                    // This represents a url with an alternative text representation
                    // For example: "Google" with this backing Url field set to http://google.com.
                    var messageEntityTextUrl = entity as MessageEntityTextUrl;
                    bubbleMarkups.Add(new BubbleMarkupTextUrl
                    {
                        Offset = (int)messageEntityTextUrl.Offset,
                        Length = (int)messageEntityTextUrl.Length,
                        Url = messageEntityTextUrl.Url
                    });
                }
                else if (entity is MessageEntityMentionName)
                {
                    // A mention with just a user's name (not username) is simpler in that
                    // the server will give us the DisaParticipant.Address.
                    var messageEntityMentionName = entity as MessageEntityMentionName;
                    var messageEntityParticipantAddress = messageEntityMentionName.UserId.ToString();
                    bubbleMarkups.Add(new BubbleMarkupMentionName
                    {
                        Offset = (int)messageEntityMentionName.Offset,
                        Length = (int)messageEntityMentionName.Length,
                        Address = messageEntityParticipantAddress,
                        IsMyself = myAddress == messageEntityParticipantAddress,
                    });
                }
            }

            return bubbleMarkups;
        }

        // Helper method for parsing: 
        // - Disa collection of BubbleMarkup
        // to
        // - Telegram collection of IMessageEntity 
        private List<IMessageEntity> HandleBubbleMarkup(List<BubbleMarkup> bubbleMarkups)
        {
            var entities = new List<IMessageEntity>();

            foreach (var bubbleMarkup in bubbleMarkups)
            {
                if (bubbleMarkup is BubbleMarkupHashtag)
                {
                    entities.Add(new MessageEntityHashtag
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                    });
                }
                else if (bubbleMarkup is BubbleMarkupBotCommand)
                {
                    entities.Add(new MessageEntityBotCommand
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                    });
                }
                else if (bubbleMarkup is BubbleMarkupUrl)
                {
                    entities.Add(new MessageEntityUrl
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                    });
                }
                else if (bubbleMarkup is BubbleMarkupEmail)
                {
                    entities.Add(new MessageEntityEmail
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                    });
                }
                else if (bubbleMarkup is BubbleMarkupBold)
                {
                    entities.Add(new MessageEntityBold
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                    });
                }
                else if (bubbleMarkup is BubbleMarkupItalic)
                {
                    entities.Add(new MessageEntityItalic
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                    });
                }
                else if (bubbleMarkup is BubbleMarkupCode)
                {
                    entities.Add(new MessageEntityCode
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                    });
                }
                else if (bubbleMarkup is BubbleMarkupPre)
                {
                    entities.Add(new MessageEntityPre
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                        Language = bubbleMarkup.Language
                    });
                }
                else if (bubbleMarkup is BubbleMarkupTextUrl)
                {
                    entities.Add(new MessageEntityTextUrl
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                        Url = bubbleMarkup.Url
                    });
                }
                // Currently we only need to grab mention of name (e.g., Bill not @Bill),
                // as we need to add in addtional info so Telegram will know which user
                // was mentioned.
                else if (bubbleMarkup is InputBubbleMarkupMentionName)
                {
                    var inputUser = new InputUser
                    {
                        UserId = uint.Parse(bubbleMarkup.Address),
                        AccessHash = GetUserAccessHashIfForeign(bubbleMarkup.Address)
                    };

                    entities.Add(new InputMessageEntityMentionName
                    {
                        Offset = (uint)bubbleMarkup.Offset,
                        Length = (uint)bubbleMarkup.Length,
                        UserId = inputUser
                    });
                }
            }

            if (entities.Count() > 0)
            {
                return entities;
            }

            return null;
        }

        // Helper method for parsing:
        // - Telegram IReplyMarkup
        // to
        // - Disa KeyboardMarkup
        private KeyboardMarkup HandleReplyMarkup(IReplyMarkup replyMarkup)
        {
            KeyboardMarkup keyboardMarkup = null;

            if (replyMarkup is ReplyKeyboardHide)
            {
                var replyKeyboardHide = replyMarkup as ReplyKeyboardHide;
                keyboardMarkup = new KeyboardMarkupHide
                {
                    Selective = replyKeyboardHide.Selective != null ? true : false
                };
            }
            else if (replyMarkup is ReplyKeyboardForceReply)
            {
                var replyKeyboardForceReply = replyMarkup as ReplyKeyboardForceReply;
                keyboardMarkup = new KeyboardMarkupForceReply
                {
                    SingleUse = replyKeyboardForceReply.SingleUse != null ? true : false,
                    Selective = replyKeyboardForceReply.Selective != null ? true : false
                };
            }
            else if (replyMarkup is ReplyKeyboardMarkup)
            {
                var replyKeyboardMarkup = replyMarkup as ReplyKeyboardMarkup;
                var keyboardCustomMarkup = new KeyboardCustomMarkup
                {
                    Resize = replyKeyboardMarkup.Resize != null ? true : false,
                    SingleUse = replyKeyboardMarkup.SingleUse != null ? true : false,
                    Selective = replyKeyboardMarkup.Selective != null ? true : false
                };

                keyboardCustomMarkup.Rows = HandleInlineKeyboardButtonRows(replyKeyboardMarkup.Rows);

                keyboardMarkup = keyboardCustomMarkup;
            }
            else if (replyMarkup is ReplyInlineMarkup)
            {
                var replyInlineMarkup = replyMarkup as ReplyInlineMarkup;
                var keyboardInlineMarkup = new KeyboardInlineMarkup();

                keyboardInlineMarkup.Rows = HandleInlineKeyboardButtonRows(replyInlineMarkup.Rows);

                keyboardMarkup = keyboardInlineMarkup;
            }

            return keyboardMarkup;
        }

        // Helper method for parsing:
        // - Telegram collection of IKeyboadButtonRow
        // to
        // - Disa collection of KeyboardButtonRow
        private List<Bots.KeyboardButtonRow> HandleInlineKeyboardButtonRows(List<SharpTelegram.Schema.IKeyboardButtonRow> rows)
        {
            var disaKeyboardMarkupRows = new List<Bots.KeyboardButtonRow>();

            if (rows == null)
            {
                return disaKeyboardMarkupRows;
            }

            foreach (var row in rows)
            {
                var telegramKeyboardButtonRow = row as SharpTelegram.Schema.KeyboardButtonRow;

                var disaKeyboardButtonRow = new Bots.KeyboardButtonRow();
                disaKeyboardMarkupRows.Add(disaKeyboardButtonRow);


                disaKeyboardButtonRow.Buttons = new List<Bots.KeyboardButton>();
                if (telegramKeyboardButtonRow != null)
                {
                    foreach (var button in telegramKeyboardButtonRow.Buttons)
                    {
                        if (button is SharpTelegram.Schema.KeyboardButton)
                        {
                            var telegramKeyboardButton = button as SharpTelegram.Schema.KeyboardButton;
                            var disaKeyboardButton = new Bots.KeyboardButtonCustom
                            {
                                Text = telegramKeyboardButton.Text
                            };
                            disaKeyboardButtonRow.Buttons.Add(disaKeyboardButton);
                        }
                        else if (button is SharpTelegram.Schema.KeyboardButtonUrl)
                        {
                            var telegramKeyboardButtonUrl = button as SharpTelegram.Schema.KeyboardButtonUrl;
                            var disaKeyboardButtonUrl = new Bots.KeyboardButtonUrl
                            {
                                Text = telegramKeyboardButtonUrl.Text,
                                Url = telegramKeyboardButtonUrl.Url
                            };
                            disaKeyboardButtonRow.Buttons.Add(disaKeyboardButtonUrl);
                        }
                        else if (button is SharpTelegram.Schema.KeyboardButtonCallback)
                        {
                            var telegramKeyboardButtonCallback = button as SharpTelegram.Schema.KeyboardButtonCallback;
                            var disaKeyboardButtonCallback = new Bots.KeyboardButtonCallback
                            {
                                Text = telegramKeyboardButtonCallback.Text,
                                Data = telegramKeyboardButtonCallback.Data
                            };
                            disaKeyboardButtonRow.Buttons.Add(disaKeyboardButtonCallback);
                        }
                        else if (button is SharpTelegram.Schema.KeyboardButtonRequestPhone)
                        {
                            var telegramKeyboardButtonRequestPhone = button as SharpTelegram.Schema.KeyboardButtonRequestPhone;
                            var disaKeyboardButtonRequestPhone = new Bots.KeyboardButtonRequestPhone
                            {
                                Text = telegramKeyboardButtonRequestPhone.Text
                            };
                            disaKeyboardButtonRow.Buttons.Add(disaKeyboardButtonRequestPhone);
                        }
                        else if (button is SharpTelegram.Schema.KeyboardButtonRequestGeoLocation)
                        {
                            var telegramKeyboardButtonRequestGeoLocation = button as SharpTelegram.Schema.KeyboardButtonRequestGeoLocation;
                            var disaKeyboardButtonRequestGeoLocation = new Bots.KeyboardButtonRequestGeoLocation
                            {
                                Text = telegramKeyboardButtonRequestGeoLocation.Text
                            };
                            disaKeyboardButtonRow.Buttons.Add(disaKeyboardButtonRequestGeoLocation);
                        }
                        else if (button is SharpTelegram.Schema.KeyboardButtonSwitchInline)
                        {
                            var telegramKeyboardButtonSwitchInline = button as SharpTelegram.Schema.KeyboardButtonSwitchInline;
                            var disaKeyboardButtonSwitchInline = new Bots.KeyboardButtonSwitchInline
                            {
                                Text = telegramKeyboardButtonSwitchInline.Text,
                                Query = telegramKeyboardButtonSwitchInline.Query
                            };
                            disaKeyboardButtonRow.Buttons.Add(disaKeyboardButtonSwitchInline);
                        }
                    }
                }
            }

            return disaKeyboardMarkupRows;
        }

        // Helper method for parsing:
        // - Disa KeyboardInlineMarkup 
        // to
        // - Telegram ReplyInlineMarkup
        private ReplyInlineMarkup HandleKeyboardInlineMarkup(Bots.KeyboardInlineMarkup keyboardInlineMarkup)
        {
            var replyMarkup = new ReplyInlineMarkup();

            replyMarkup.Rows = HandleKeyboardButtonRows(keyboardInlineMarkup.Rows);
            
            return replyMarkup;
        }

        // Helper method for parsing:
        // - Disa collection of KeyboardButtonRow
        // to 
        // - Telegram collection of IKeyboardButtonRow
        private List<SharpTelegram.Schema.IKeyboardButtonRow> HandleKeyboardButtonRows(List<Bots.KeyboardButtonRow> rows)
        {
            List<SharpTelegram.Schema.IKeyboardButtonRow> telegramKeyboardButtonRows = new List<SharpTelegram.Schema.IKeyboardButtonRow>();

            if (rows == null)
            {
                return telegramKeyboardButtonRows;
            }

            foreach (var disaKeyboardButtonRow in rows)
            {
                var telegramKeyboardButtonRow = new SharpTelegram.Schema.KeyboardButtonRow();
                telegramKeyboardButtonRows.Add(telegramKeyboardButtonRow);

                telegramKeyboardButtonRow.Buttons = new List<IKeyboardButton>();
                if (disaKeyboardButtonRow != null)
                {
                    foreach (var button in disaKeyboardButtonRow.Buttons)
                    {
                        if (button is Bots.KeyboardButtonCustom)
                        {
                            var disaKeyboardButtonCustom = button as Bots.KeyboardButtonCustom;
                            var telegramKeyboardButton = new SharpTelegram.Schema.KeyboardButton
                            {
                                Text = disaKeyboardButtonCustom.Text
                            };
                            telegramKeyboardButtonRow.Buttons.Add(telegramKeyboardButton);
                        }
                        else if (button is Bots.KeyboardButtonUrl)
                        {
                            var disaKeyboardButtonUrl = button as Bots.KeyboardButtonUrl;
                            var telegramKeyboardButtonUrl = new SharpTelegram.Schema.KeyboardButtonUrl
                            {
                                Text = disaKeyboardButtonUrl.Text,
                                Url = disaKeyboardButtonUrl.Url
                            };
                            telegramKeyboardButtonRow.Buttons.Add(telegramKeyboardButtonUrl);
                        }
                        else if (button is Bots.KeyboardButtonCallback)
                        {
                            var disaKeyboardButtonCallback = button as Bots.KeyboardButtonCallback;
                            var telegramKeyboardButtonCallback = new SharpTelegram.Schema.KeyboardButtonCallback
                            {
                                Text = disaKeyboardButtonCallback.Text,
                                Data = disaKeyboardButtonCallback.Data
                            };
                            telegramKeyboardButtonRow.Buttons.Add(telegramKeyboardButtonCallback);
                        }
                        else if (button is Bots.KeyboardButtonRequestPhone)
                        {
                            var disaKeyboardButtonRequestPhone = button as Bots.KeyboardButtonRequestPhone;
                            var telegramKeyboardButtonRequestPhone = new SharpTelegram.Schema.KeyboardButtonRequestPhone
                            {
                                Text = disaKeyboardButtonRequestPhone.Text
                            };
                            telegramKeyboardButtonRow.Buttons.Add(telegramKeyboardButtonRequestPhone);
                        }
                        else if (button is Bots.KeyboardButtonRequestGeoLocation)
                        {
                            var disaKeyboardButtonRequestGeoLocation = button as Bots.KeyboardButtonRequestGeoLocation;
                            var telegramKeyboardButtonRequestGeoLocation = new SharpTelegram.Schema.KeyboardButtonRequestGeoLocation
                            {
                                Text = disaKeyboardButtonRequestGeoLocation.Text
                            };
                            telegramKeyboardButtonRow.Buttons.Add(telegramKeyboardButtonRequestGeoLocation);
                        }
                        else if (button is Bots.KeyboardButtonSwitchInline)
                        {
                            var disaKeyboardButtonSwitchInline = button as Bots.KeyboardButtonSwitchInline;
                            var telegramKeyboardButtonSwitchInline = new SharpTelegram.Schema.KeyboardButtonSwitchInline
                            {
                                Text = disaKeyboardButtonSwitchInline.Text,
                                Query = disaKeyboardButtonSwitchInline.Query
                            };
                            telegramKeyboardButtonRow.Buttons.Add(telegramKeyboardButtonSwitchInline);
                        }
                    }
                }
            }

            return telegramKeyboardButtonRows;
        }

        private List<VisualBubble> MakePartyInformationBubble(MessageService messageService, bool useCurrentTime, TelegramClient optionalClient)
	    {
            var editTitle = messageService.Action as MessageActionChatEditTitle;
            var deleteUser = messageService.Action as MessageActionChatDeleteUser;
            var addUser = messageService.Action as MessageActionChatAddUser;
            var created = messageService.Action as MessageActionChatCreate;
            var thumbnailChanged = messageService.Action as MessageActionChatEditPhoto;
            var upgradedToSuperGroup = messageService.Action as MessageActionChannelMigrateFrom;
            var chatMigaratedToSuperGroup = messageService.Action as MessageActionChatMigrateTo;
            var chatJoinedByInviteLink = messageService.Action as MessageActionChatJoinedByLink;

            var address = TelegramUtils.GetPeerId(messageService.ToId);
            var fromId = messageService.FromId.ToString(CultureInfo.InvariantCulture);

            if (editTitle != null)
            {
                var newTitle = editTitle.Title;
                var chatToUpdate = _dialogs.GetChat(uint.Parse(address));
                if (chatToUpdate != null)
                {
                    TelegramUtils.SetChatTitle(chatToUpdate, newTitle);
                }
                var bubble = PartyInformationBubble.CreateTitleChanged(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId, newTitle);
                bubble.IsServiceIdSequence = true;
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
                BubbleGroupUpdater.Update(this, address);
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (thumbnailChanged != null)
            {
                InvalidateThumbnail(address, true, true);
                InvalidateThumbnail(address, true, false);
                var bubble = PartyInformationBubble.CreateThumbnailChanged(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(), null);
                bubble.IsServiceIdSequence = true;
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (deleteUser != null)
            {
                var userDeleted = deleteUser.UserId.ToString(CultureInfo.InvariantCulture);
                var bubble = PartyInformationBubble.CreateParticipantRemoved(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId, userDeleted);
                bubble.IsServiceIdSequence = true;
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (addUser != null)
            {
                var returnList = new List<VisualBubble>();
                foreach (var userId in addUser.Users)
                {
                    var userAdded = userId.ToString(CultureInfo.InvariantCulture);
                    var bubble = PartyInformationBubble.CreateParticipantAdded(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                        this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId, userAdded);
                    bubble.IsServiceIdSequence = true;
                    if (messageService.ToId is PeerChannel)
                    {
                        bubble.ExtendedParty = true;
                    }
                    returnList.Add(bubble);
                }
                return returnList;
            }
            else if (created != null)
            {
                var bubble = PartyInformationBubble.CreateParticipantAdded(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId,
                    _settings.AccountId.ToString(CultureInfo.InvariantCulture));
                bubble.IsServiceIdSequence = true;
				
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
				var shortMessageChat = _dialogs.GetChat(uint.Parse(address));
				if (shortMessageChat == null)
				{
					DebugPrint(">>>>> Chat is null, fetching user from the server");
					GetMessage(messageService.Id, optionalClient);
				}
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (chatJoinedByInviteLink != null)
            { 
                var bubble = PartyInformationBubble.CreateParticipantAdded(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address,
                    this, messageService.Id.ToString(CultureInfo.InvariantCulture), fromId,
                    _settings.AccountId.ToString(CultureInfo.InvariantCulture));
                bubble.IsServiceIdSequence = true;
                if (messageService.ToId is PeerChannel)
                {
                    bubble.ExtendedParty = true;
                }
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (upgradedToSuperGroup != null)
            {
                var bubble = PartyInformationBubble.CreateConvertedToExtendedParty(
                    useCurrentTime ? Time.GetNowUnixTimestamp() : (long)messageService.Date, address, this,
                    messageService.Id.ToString(CultureInfo.InvariantCulture));
                bubble.ExtendedParty = true;
                bubble.IsServiceIdSequence = true;
                return new List<VisualBubble>
                {
                    bubble
                };
            }
            else if (chatMigaratedToSuperGroup != null)
            {
                if (!_oldMessages)
                {
                    var bubbleGroupToSwitch = BubbleGroupManager.FindWithAddress(this, chatMigaratedToSuperGroup.ChannelId.ToString());
                    var bubbleGroupToDelete = BubbleGroupManager.FindWithAddress(this, address);
                    if (bubbleGroupToSwitch != null)
                    {
						if (Platform.GetCurrentBubbleGroupOnUI()?.Address == bubbleGroupToDelete?.Address)
						{
                            Platform.SwitchCurrentBubbleGroupOnUI(bubbleGroupToSwitch);
						}
                    }
                }
                return new List<VisualBubble>();
            }
            else
            {
                DebugPrint("Unknown message service: " + ObjectDumper.Dump(messageService));
                return new List<VisualBubble>();
            }
        }

        private void AddQuotedMessageToBubble(Message replyMessage, VisualBubble bubble)
        {
            if (replyMessage == null)
            {
                return;
            }

            if(!string.IsNullOrEmpty(replyMessage.MessageProperty))
            {
                bubble.QuotedType = VisualBubble.MediaType.Text;
                bubble.QuotedContext = replyMessage.MessageProperty;

            }
            else
            {
                var messageMedia = replyMessage.Media;
                var messageMediaPhoto = messageMedia as MessageMediaPhoto;
                var messageMediaDocument = messageMedia as MessageMediaDocument;
                var messageMediaGeo = messageMedia as MessageMediaGeo;
                var messageMediaVenue = messageMedia  as MessageMediaVenue;
                var messageMediaContact = messageMedia as MessageMediaContact;

                if (messageMediaPhoto != null)
                {
                    bubble.QuotedType = VisualBubble.MediaType.Image;
                    bubble.HasQuotedThumbnail = true;
                    bubble.QuotedThumbnail = GetCachedPhotoBytes(messageMediaPhoto.Photo);
                }
                if(messageMediaDocument != null)
                {
                    var document = messageMediaDocument.Document as SharpTelegram.Schema.Document;
                    if(document!=null)
                    {
                        var stickerAlt = document.Attributes
                                                 .OfType<SharpTelegram.Schema.DocumentAttributeSticker>()
                                                 .FirstOrDefault()?.Alt;
                        if (stickerAlt != null)
                        {
                            bubble.QuotedType = VisualBubble.MediaType.Sticker;
                            bubble.QuotedContext = stickerAlt;
                        }
                        else if (document.MimeType.Contains("audio"))
                        {
                            bubble.QuotedType = VisualBubble.MediaType.Audio;
                            bubble.QuotedSeconds = GetAudioTime(document);
                        }
                        else if (document.MimeType.Contains("video"))
                        {
                            bubble.QuotedType = VisualBubble.MediaType.Video;
                            bubble.QuotedContext = "Video";
                        }
                        else
                        {
                            bubble.QuotedType = VisualBubble.MediaType.File;
                            bubble.QuotedContext = GetDocumentFileName(document);
                        }

                    }
                }
                if(messageMediaGeo != null)
                {
                    var geoPoint = messageMediaGeo.Geo as SharpTelegram.Schema.GeoPoint;

                    if (geoPoint != null)
                    {
                        bubble.QuotedType = VisualBubble.MediaType.Location;
                        bubble.QuotedContext = (int)geoPoint.Lat + "," + (int)geoPoint.Long;
                        bubble.QuotedLocationLatitude = geoPoint.Lat;
                        bubble.QuotedLocationLongitude = geoPoint.Long;
                        bubble.HasQuotedThumbnail = true;
                    }
                        
                }
                if(messageMediaVenue != null)
                {
                    var geoPoint = messageMediaVenue.Geo as SharpTelegram.Schema.GeoPoint;

                    if (geoPoint != null)
                    {
                        bubble.QuotedType = VisualBubble.MediaType.Location;
                        bubble.QuotedContext = messageMediaVenue.Title;
                        bubble.QuotedLocationLatitude = geoPoint.Lat;
                        bubble.QuotedLocationLongitude = geoPoint.Long;
                        bubble.HasQuotedThumbnail = true;
                    }

                }
                if(messageMediaContact != null)
                {
                    bubble.QuotedType = VisualBubble.MediaType.Contact;
                    bubble.QuotedContext = messageMediaContact.FirstName + " " + messageMediaContact.LastName; 
                }
                    
            }
            bubble.QuotedAddress = replyMessage.FromId.ToString(CultureInfo.InvariantCulture);
            bubble.QuotedIdService = replyMessage.Id.ToString(CultureInfo.InvariantCulture);

        }

        private List<VisualBubble> ProcessFullMessage(Message message, bool useCurrentTime, TelegramClient optionalClient = null)
        {
            var peerUser = message.ToId as PeerUser;
            var peerChat = message.ToId as PeerChat;
            var peerChannel = message.ToId as PeerChannel;

            var direction = message.FromId == _settings.AccountId
                ? Bubble.BubbleDirection.Outgoing
                : Bubble.BubbleDirection.Incoming;

            var bubblesReturn = new List<VisualBubble>();

            if (!string.IsNullOrWhiteSpace(message.MessageProperty))
            {
                TextBubble tb = null;
                string address = null;
                if (peerUser != null)
                {
                    var addressId = direction == Bubble.BubbleDirection.Incoming
                        ? message.FromId
                        : peerUser.UserId;
                    address = addressId.ToString(CultureInfo.InvariantCulture);
                    tb = new TextBubble(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                        direction, address, null, false, this, message.MessageProperty,
                        message.Id.ToString(CultureInfo.InvariantCulture));
                }
                else if (peerChat != null)
                {
                    address = peerChat.ChatId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    tb = new TextBubble(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                        direction, address, participantAddress, true, this, message.MessageProperty,
                        message.Id.ToString(CultureInfo.InvariantCulture));
                }
                else if (peerChannel != null) 
                {
                    address = peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    tb = new TextBubble(
                        useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                        direction, address, participantAddress, true, this, message.MessageProperty,
                        message.Id.ToString(CultureInfo.InvariantCulture));
                    tb.ExtendedParty = true;
                }
                if (tb != null)
                {
                    if (direction == Bubble.BubbleDirection.Outgoing)
                    {
                        tb.Status = Bubble.BubbleStatus.Sent;
                    }

                    // Do we have any mentions to process?
                    if (message.Entities != null)
                    {
                        tb.BubbleMarkups = HandleEntities(
                            message: message.MessageProperty,
                            entities: message.Entities,
                            bubbleGroupAddress: address,
                            extendedParty: tb.ExtendedParty, 
                            optionalClient: optionalClient);
                    }

                    // Do we have any inline keyboard to process?
                    if (message.ReplyMarkup != null)
                    {
                        tb.KeyboardMarkup = HandleReplyMarkup(message.ReplyMarkup);
                    }

                    // Was this sent by a bot (id will be greater than 0)?
                    tb.ViaBotId = message.ViaBotId > 0 ? message.ViaBotId.ToString() : null;

                    bubblesReturn.Add(tb);
                }
            }
            else
            {
                if (peerUser != null)
                {
                    var address = direction == Bubble.BubbleDirection.Incoming
                        ? message.FromId
                        : peerUser.UserId;
                    var addressStr = address.ToString(CultureInfo.InvariantCulture);
                    var bubble = MakeMediaBubble(message, useCurrentTime, true, addressStr);
                    bubblesReturn.AddRange(bubble);
                }
                else if (peerChat != null)
                {
                    var address = peerChat.ChatId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    var bubble = MakeMediaBubble(message, useCurrentTime, false, address, participantAddress);
                    bubblesReturn.AddRange(bubble);
                }
                else if (peerChannel != null)
                {
                    var address = peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture);
                    var participantAddress = message.FromId.ToString(CultureInfo.InvariantCulture);
                    var bubbles = MakeMediaBubble(message, useCurrentTime, false, address, participantAddress);
                    foreach (var bubble in bubbles)
                    {
                        bubble.ExtendedParty = true;   
                    }
                    bubblesReturn.AddRange(bubbles);
                }
            }

            foreach (var bubble in bubblesReturn)
            {
                if (bubble.ParticipantAddress == "0")
                {
                    bubble.ParticipantAddress = VisualBubble.NonSignedChannel;
                }

                bubble.IsServiceIdSequence = true;
            }
            return bubblesReturn;
        }

        private IMessagesMessages FetchMessage(uint id, TelegramClient client, uint toId, bool superGroup)
        {
            try
            {
                if (superGroup)
                {
                    return TelegramUtils.RunSynchronously(client.Methods.ChannelsGetMessagesAsync(new ChannelsGetMessagesArgs
                    {
                        Channel = new InputChannel
                        {
                            ChannelId = toId,
                            AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(toId))
                        },
                        Id = new List<uint>
                        {
                            id
                        }
                    }));
                }
                else
                {
                    return TelegramUtils.RunSynchronously(client.Methods.MessagesGetMessagesAsync(new MessagesGetMessagesArgs
                    {
                        Id = new List<uint>
                        {
                            id
                        }
                    }));
                }
            }
            catch (Exception e)
            {
                DebugPrint("Exeption while fetching message: " + e);
                return MakeMessagesMessages(new List<IChat>(), new List<IUser>(), new List<IMessage>());
            }
        }

        private IMessage GetMessage(uint messageId, TelegramClient optionalClient, uint senderId = 0, bool superGroup = false)
        {
            Func<TelegramClient,IMessage> fetch = client =>
            {
                var messagesmessages = FetchMessage(messageId, client, senderId, superGroup);
                var messages = TelegramUtils.GetMessagesFromMessagesMessages(messagesmessages);
                var chats = TelegramUtils.GetChatsFromMessagesMessages(messagesmessages);
                var users = TelegramUtils.GetUsersFromMessagesMessages(messagesmessages);

                _dialogs.AddUsers(users);
                _dialogs.AddChats(chats);

                if (messages != null && messages.Count > 0)
                {
                    return messages[0];
                }
                return null;
            };

            if (optionalClient != null)
            {
                return fetch(optionalClient);
            }
            else
            {
                using (var client = new FullClientDisposable(this))
                {
                    return fetch(client.Client);
                }
            }  
        }

        private List<VisualBubble> MakeMediaBubble(Message message, bool useCurrentTime, bool isUser, string addressStr, string participantAddress = null)
        {
            var messageMedia = message.Media;
            var messageMediaPhoto = messageMedia as MessageMediaPhoto;
            var messageMediaDocument = messageMedia as MessageMediaDocument;
            var messageMediaGeo = messageMedia as MessageMediaGeo;
            var messageMediaVenue = messageMedia as MessageMediaVenue;
            var messageMediaContact = messageMedia as MessageMediaContact;

            if (messageMediaPhoto != null)
            {
                var fileLocation = GetPhotoFileLocation(messageMediaPhoto.Photo);
                var fileSize = GetPhotoFileSize(messageMediaPhoto.Photo);
                var dimensions = GetPhotoDimensions(messageMediaPhoto.Photo);
                var cachedPhoto = GetCachedPhotoBytes(messageMediaPhoto.Photo);
                FileInformation fileInfo = new FileInformation
                {
                    FileLocation = fileLocation,
                    Size = fileSize,
                    FileType = "image",
                    Document = new SharpTelegram.Schema.Document()
                };
                using (var memoryStream = new MemoryStream())
                {
                    Serializer.Serialize<FileInformation>(memoryStream, fileInfo);
                    ImageBubble imageBubble;
                    if (isUser)
                    {
                        imageBubble = new ImageBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                            message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                            addressStr, null, false, this, null, ImageBubble.Type.Url,
                            cachedPhoto, message.Id.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        imageBubble = new ImageBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                            message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                            addressStr, participantAddress, true, this, null,
                            ImageBubble.Type.Url, cachedPhoto, message.Id.ToString(CultureInfo.InvariantCulture));
                    }
                    if (imageBubble.Direction == Bubble.BubbleDirection.Outgoing)
                    {
                        imageBubble.Status = Bubble.BubbleStatus.Sent;
                    }
                    imageBubble.AdditionalData = memoryStream.ToArray();
                    imageBubble.Width = dimensions.Width;
                    imageBubble.Height = dimensions.Height;
                    imageBubble.ViaBotId = message.ViaBotId > 0 ? message.ViaBotId.ToString() : null;
                    imageBubble.Caption = messageMediaPhoto.Caption;

                    var returnList = new List<VisualBubble>
                    {
                        imageBubble  
                    };

                    return returnList;
                }
                
            }
            else if (messageMediaDocument != null)
            {
                var document = messageMediaDocument.Document as SharpTelegram.Schema.Document;
                if (document != null)
                {
                    FileInformation fileInfo = new FileInformation
                    {
                        FileType = "document",
                        Document = document
                    };
                    using (var memoryStream = new MemoryStream())
                    {
                        Serializer.Serialize<FileInformation>(memoryStream, fileInfo);
                        VisualBubble bubble;
                        if (document.MimeType.Contains("audio"))
                        {
                            var audioTime = (int) GetAudioTime(document);
                            if (isUser)
                            {
                                bubble =
                                    new AudioBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                                        message.Out != null
                                            ? Bubble.BubbleDirection.Outgoing
                                            : Bubble.BubbleDirection.Incoming, addressStr, null, false, this, "",
                                        AudioBubble.Type.Url,
                                        false, audioTime, message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                bubble =
                                    new AudioBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long) message.Date,
                                        message.Out != null
                                            ? Bubble.BubbleDirection.Outgoing
                                            : Bubble.BubbleDirection.Incoming, addressStr, participantAddress, true,
                                        this, "",
                                        AudioBubble.Type.Url, false, audioTime,
                                        message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                        else
                        {
                            //TODO: localize
                            var filename = document.MimeType.Contains("video")
                                ? ""
                                : GetDocumentFileName(document);

                            var isSticker = document.Attributes.FirstOrDefault(x => x is 
                                                   SharpTelegram.Schema.DocumentAttributeSticker) != null;
							uint width = 0;
							uint height = 0;
							var photoSize = document.Thumb as SharpTelegram.Schema.PhotoSize;
                            if (photoSize != null)
                            {
                                width = photoSize.W;
                                height = photoSize.H;
                            }
                            if (isUser)
                            {
                                if (isSticker)
                                {
                                    var stickerAlt = document.Attributes
                                                             .OfType<SharpTelegram.Schema.DocumentAttributeSticker>()
                                                             .FirstOrDefault()?.Alt;
                                    var stickerBubble =
                                        new StickerBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                            message.Out != null
                                                ? Bubble.BubbleDirection.Outgoing
                                                : Bubble.BubbleDirection.Incoming, addressStr, null, false, this, null,
                                                      StickerBubble.Type.File, (int)width, (int)height, null, null,
                                                          message.Id.ToString(CultureInfo.InvariantCulture));
                                    stickerBubble.AlternativeEmoji = stickerAlt;
                                    bubble = stickerBubble;
                                }
                                else
                                {
                                    bubble =
                                        new FileBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                            message.Out != null
                                                ? Bubble.BubbleDirection.Outgoing
                                                : Bubble.BubbleDirection.Incoming, addressStr, null, false, this, "",
                                            FileBubble.Type.Url, filename, document.MimeType,
                                            message.Id.ToString(CultureInfo.InvariantCulture));
                                }
                            }
                            else
                            {
                                if (isSticker)
                                {
                                    var stickerAlt = document.Attributes
                                         .OfType<SharpTelegram.Schema.DocumentAttributeSticker>()
                                         .FirstOrDefault()?.Alt;
                                    var stickerBubble =
                                        new StickerBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                            message.Out != null
                                                ? Bubble.BubbleDirection.Outgoing
                                                : Bubble.BubbleDirection.Incoming, addressStr, participantAddress, true,
                                            this, null, StickerBubble.Type.File, (int)width, (int)height, null, null,
                                            message.Id.ToString(CultureInfo.InvariantCulture));
                                    stickerBubble.AlternativeEmoji = stickerAlt;
                                    bubble = stickerBubble;
                                }
                                else
                                {
                                    bubble =
                                        new FileBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                            message.Out != null
                                                ? Bubble.BubbleDirection.Outgoing
                                                : Bubble.BubbleDirection.Incoming, addressStr, participantAddress, true,
                                            this, "", FileBubble.Type.Url, filename, document.MimeType,
                                            message.Id.ToString(CultureInfo.InvariantCulture));
                                }
                            }

                        }

                        if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
                        {
                            bubble.Status = Bubble.BubbleStatus.Sent;
                        }
                        bubble.AdditionalData = memoryStream.ToArray();
                        bubble.ViaBotId = message.ViaBotId > 0 ? message.ViaBotId.ToString() : null;

                        var returnList = new List<VisualBubble>
                        {
                            bubble
                        };
                        if (!string.IsNullOrEmpty(messageMediaDocument.Caption))
                        {
                            TextBubble captionBubble = null;
                            if (isUser)
                            {
                                captionBubble = new TextBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                    addressStr, null, false, this, messageMediaDocument.Caption,
                                    message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                captionBubble = new TextBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming,
                                    addressStr, participantAddress, true, this, messageMediaDocument.Caption,
                                    message.Id.ToString(CultureInfo.InvariantCulture));
                            }
                            captionBubble.ViaBotId = message.ViaBotId > 0 ? message.ViaBotId.ToString() : null;
                            returnList.Add(captionBubble);
                        }
                        return returnList;
                    }

                }

            }
            else if (messageMediaGeo != null)
            {

                var geoPoint = messageMediaGeo.Geo as SharpTelegram.Schema.GeoPoint;

                if (geoPoint != null)
                {
                    var geoBubble = MakeGeoBubble(geoPoint, message, isUser, useCurrentTime, addressStr, participantAddress, null);
                    geoBubble.ViaBotId = message.ViaBotId > 0 ? message.ViaBotId.ToString() : null;
                    return new List<VisualBubble>
                    {
                        geoBubble
                    };
                }


            }
            else if (messageMediaVenue != null)
            {
                var geoPoint = messageMediaVenue.Geo as SharpTelegram.Schema.GeoPoint;

                if (geoPoint != null)
                {
                    var geoBubble = MakeGeoBubble(geoPoint,message,isUser,useCurrentTime,addressStr,participantAddress,messageMediaVenue.Title);
                    geoBubble.ViaBotId = message.ViaBotId > 0 ? message.ViaBotId.ToString() : null;
                    return new List<VisualBubble>
                    {
                        geoBubble
                    };
                }

            }
            else if (messageMediaContact != null)
            {
                var contactCard = new ContactCard
                {
                    GivenName = messageMediaContact.FirstName,
                    FamilyName = messageMediaContact.LastName
                };
                contactCard.Phones.Add(new ContactCard.ContactCardPhone
                {
                    Number = messageMediaContact.PhoneNumber
                });
                var vCardData = Platform.GenerateBytesFromContactCard(contactCard);
                var contactBubble = MakeContactBubble(message, isUser, useCurrentTime, addressStr, participantAddress, vCardData, messageMediaContact.FirstName);
                contactBubble.ViaBotId = message.ViaBotId > 0 ? message.ViaBotId.ToString() : null;

                return new List<VisualBubble>
                { 
                    contactBubble
                };
            }

            return new List<VisualBubble>();
        }

        private ContactBubble MakeContactBubble(Message message, bool isUser, bool useCurrentTime, string addressStr, string participantAddress, byte[] vCardData, string name)
        {
            ContactBubble bubble;

            if (isUser)
            {
                bubble = new ContactBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                         message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr,
                         null, false, this, name, vCardData,
                         message.Id.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                bubble = new ContactBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                         message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr,
                         participantAddress, true, this, name, vCardData,
                         message.Id.ToString(CultureInfo.InvariantCulture));
            }
            if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
            {
                bubble.Status = Bubble.BubbleStatus.Sent;
            }

            return bubble;
        }

        private VisualBubble MakeGeoBubble(SharpTelegram.Schema.GeoPoint geoPoint,Message message,bool isUser,bool useCurrentTime,string addressStr,string participantAddress,string name)
        {
            LocationBubble bubble;
            if (isUser)
            {
                bubble = new LocationBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr, null, false, this, geoPoint.Long, geoPoint.Lat,
                    "", null, message.Id.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                bubble = new LocationBubble(useCurrentTime ? Time.GetNowUnixTimestamp() : (long)message.Date,
                    message.Out != null ? Bubble.BubbleDirection.Outgoing : Bubble.BubbleDirection.Incoming, addressStr, participantAddress, true, this, geoPoint.Long, geoPoint.Lat,
                    "", null, message.Id.ToString(CultureInfo.InvariantCulture));
            }
            if (name != null) 
            {
                bubble.Name = name;
            }
            if (bubble.Direction == Bubble.BubbleDirection.Outgoing)
            {
                bubble.Status = Bubble.BubbleStatus.Sent;
            }
            return bubble;
        }

        private string GetDocumentFileName(SharpTelegram.Schema.Document document)
        {
            foreach (var attribute in document.Attributes)
            {
                var attributeFilename = attribute as SharpTelegram.Schema.DocumentAttributeFilename;
                if (attributeFilename != null)
                {
                    return attributeFilename.FileName;
                }
            }
            return null;
        }

        private uint GetAudioTime(SharpTelegram.Schema.Document document)
        {
            foreach (var attribute in document.Attributes)
            {
                var attributeAudio = attribute as SharpTelegram.Schema.DocumentAttributeAudio;
                if (attributeAudio != null)
                {
                    return attributeAudio.Duration;
                }
            }
            return 0;
        }

        private uint GetPhotoFileSize(IPhoto iPhoto)
        {
            var photo = iPhoto as SharpTelegram.Schema.Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeNormal = photoSize as SharpTelegram.Schema.PhotoSize;
                    if (photoSizeNormal != null)
                    {
                        if (photoSizeNormal.Type == "x")
                        {
                            return photoSizeNormal.Size;
                        }
                    }

                }
            }
            return 0;
        }

        private Size GetPhotoDimensions(IPhoto iPhoto)
        {
            var photo = iPhoto as SharpTelegram.Schema.Photo;

            if (photo != null)
            {
                // Try and fetch the cached size.
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeCached = photoSize as SharpTelegram.Schema.PhotoCachedSize;
                    if (photoSizeCached != null)
                    {
                        return new Size((int)photoSizeCached.W, (int)photoSizeCached.H);
                    }
                }

                // Can't find the cached size? Use the downloaded size.
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeNormal = photoSize as SharpTelegram.Schema.PhotoSize;
                    if (photoSizeNormal != null)
                    {
                        if (photoSizeNormal.Type == "x")
                        {
                            return new Size((int)photoSizeNormal.W, (int)photoSizeNormal.H);
                        }
                    }
                }
            }

            return new Size(0, 0);
        }

        private SharpTelegram.Schema.FileLocation GetPhotoFileLocation(IPhoto iPhoto)
        {
            var photo = iPhoto as SharpTelegram.Schema.Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeNormal = photoSize as SharpTelegram.Schema.PhotoSize;
                    if (photoSizeNormal != null)
                    {
                        if (photoSizeNormal.Type == "x")
                        {
                            return photoSizeNormal.Location as SharpTelegram.Schema.FileLocation;
                        }
                    }

                }
            }
            return null;
        }


        private SharpTelegram.Schema.FileLocation GetCachedPhotoFileLocation(IPhoto iPhoto)
        {
            var photo = iPhoto as SharpTelegram.Schema.Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeSmall = photoSize as SharpTelegram.Schema.PhotoSize;
                    if (photoSizeSmall != null)
                    {
                        if (photoSizeSmall.Type == "s")
                        {
                            return photoSizeSmall.Location as SharpTelegram.Schema.FileLocation;
                        }
                    }

                }
            }
            return null;
        }

        private byte[] GetCachedPhotoBytes(IPhoto iPhoto)
        {
            var photo = iPhoto as SharpTelegram.Schema.Photo;

            if (photo != null)
            {
                foreach (var photoSize in photo.Sizes)
                {
                    var photoSizeCached = photoSize as SharpTelegram.Schema.PhotoCachedSize;
                    if (photoSizeCached != null)
                    {
                        return photoSizeCached.Bytes;
                    }

                }
            }
            return null;
        }


        private class OptionalClientDisposable : IDisposable
        {
            private readonly TelegramClient _optionalClient;
            private readonly FullClientDisposable _fullClient;

            public OptionalClientDisposable(Telegram telegram, TelegramClient optionalClient = null)
            {
                _optionalClient = optionalClient;
                if (_optionalClient == null)
                {
                    _fullClient = new FullClientDisposable(telegram);
                }
            }

            public TelegramClient Client
            {
                get
                {
                    if (_optionalClient != null)
                    {
                        return _optionalClient;
                    }
                    else
                    {
                        return _fullClient.Client;
                    }
                }
            }

            public void Dispose()
            {
                if (_fullClient != null)
                {
                    _fullClient.Dispose();
                }
            }
        }

        private void SendReceivedMessages(TelegramClient optionalClient, uint maxId)
        {
            Task.Factory.StartNew(() =>
            {
                using (var disposable = new OptionalClientDisposable(this, optionalClient))
                {
                    var items = TelegramUtils.RunSynchronously(disposable.Client.Methods
                        .MessagesReceivedMessagesAsync(new MessagesReceivedMessagesArgs
                    {
                            MaxId = maxId,
                    }));
                }
            }); 
        }
            
        private void OnLongPollClientClosed(object sender, EventArgs e)
        {
            if (!_longPollerAborted)
            {
                Utils.DebugPrint("Looks like a long poll client closed itself internally. Restarting Telegram...");
                RestartTelegram(null);
            }
        }

        private void OnLongPollClientUpdateTooLong(object sender, EventArgs e)
        {
            if (IsFullClientConnected)
                return;
            Task.Factory.StartNew(() =>
            {
                var transportConfig = 
                    new TcpClientTransportConfig(_settings.NearestDcIp, _settings.NearestDcPort);
                using (var client = new TelegramClient(transportConfig, 
                    new ConnectionConfig(_settings.AuthKey, _settings.Salt), AppInfo))
                {
                    MTProtoConnectResult result = MTProtoConnectResult.Other;
                    try
                    {
                        result = TelegramUtils.RunSynchronously(client.Connect());
                    }
                    catch (Exception ex)
                    {
                        DebugPrint("Exception while connecting " + ex);
                    }
                    if (result != MTProtoConnectResult.Success)
                    {
                        throw new Exception("Failed to connect: " + result);
                    }  
                    FetchState(client);
                }
            });
        }

        private void SendPresence(TelegramClient client)
        {
            TelegramUtils.RunSynchronously(client.Methods.AccountUpdateStatusAsync(
                new AccountUpdateStatusArgs
            {
                Offline = !_hasPresence
            }));
        }

        private void Ping(TelegramClient client, Action<Exception> exception = null)
        {
            try
            {
                Utils.DebugPrint("Sending ping!");
                var pong = (Pong)TelegramUtils.RunSynchronously(client.ProtoMethods.PingAsync(new PingArgs
                {
                    PingId = GetRandomId(),
                }));
                Utils.DebugPrint("Got pong (from ping): " + pong.MsgId);
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Failed to ping client: " + ex);
                if (exception != null)
                {
                    exception(ex);
                }
            }
        }
            
        private async void PingDelay(TelegramClient client, uint disconnectDelay, Action<Exception> exception = null)
        {
            try
            {
                Utils.DebugPrint(">>>>>>>>> Sending pingDelay!");
                var pong = (Pong)await client.ProtoMethods.PingDelayDisconnectAsync(new PingDelayDisconnectArgs
                {
                    PingId = GetRandomId(),
                    DisconnectDelay = disconnectDelay
                });
                Utils.DebugPrint(">>>>>>>>>> Got pong (from pingDelay): " + pong.MsgId);
            }
            catch (Exception ex)
            {
                if (exception != null)
                {
                    exception(ex);
                }
            }
        }

        private void RestartTelegram(Exception exception)
        {
            if (exception != null)
            {
                Utils.DebugPrint("Restarting Telegram: " + exception);
            }
            else
            {
                Utils.DebugPrint("Restarting Telegram");
            }
            // start a new task, freeing the possibility that there could be a wake lock being held
            Task.Factory.StartNew(() =>
            {
                ServiceManager.Restart(this);
            });
        }

        private void ScheduleLongPollPing()
        {
            RemoveLongPollPingIfPossible();
            _longPollHeartbeart = new WakeLockBalancer.GracefulWakeLock(new WakeLockBalancer.ActionObject(() =>
            {
                if (_longPollClient == null || !_longPollClient.IsConnected)
                {
                    RemoveLongPollPingIfPossible();
                    RestartTelegram(null);
                }
                else
                {
                    Ping(_longPollClient, RestartTelegram);
                }
            }, WakeLockBalancer.ActionObject.ExecuteType.TaskWithWakeLock), 240, 60, true);
            Platform.ScheduleAction(_longPollHeartbeart);
        }

        private void RemoveLongPollPingIfPossible()
        {
            if (_longPollHeartbeart != null)
            {
                Platform.RemoveAction(_longPollHeartbeart);
                _longPollHeartbeart = null;
            }
        }

        private void DisconnectLongPollerIfPossible()
        {
            if (_longPollClient != null && _longPollClient.IsConnected)
            {
                _longPollerAborted = true;
                try
                {
                    TelegramUtils.RunSynchronously(_longPollClient.Disconnect());
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to disconnect full client: " + ex);
                }
                RemoveLongPollPingIfPossible();
            }
        }

        private ulong GetRandomId()
        {
            var buffer = new byte[sizeof(ulong)];
            _random.NextBytes(buffer);
            return BitConverter.ToUInt32(buffer, 0);
        }

        public Telegram()
        {
            _baseMessageId = Convert.ToString(Time.GetNowUnixTimestamp());
        }

        public override bool Initialize(DisaSettings settings)
        {
            _settings = settings as TelegramSettings;
            _mutableSettings = MutableSettingsManager.Load<TelegramMutableSettings>();

            if (_settings.AuthKey == null)
            {
                return false;
            }

            return true;
        }

        public override bool InitializeDefault()
        {
            return false;
        }

        public override bool Authenticate(WakeLock wakeLock)
        {
            return true;
        }

        public override void Deauthenticate()
        {
            // do nothing
        }

        private void FetchDifference(TelegramClient client)
        {
            var counter = 0;

            DebugPrint("Fetching difference");

            //sometimes fetchdifference gives new channel message, which updates the state of the channel 
            //which leads to missed messasges, hence we should first fetch difference for the channels, then fetch the total 
            //difference.
            FetchChannelDifference(client);

        Again:

            DebugPrint("Difference Page: " + counter);

            var difference = TelegramUtils.RunSynchronously(
                client.Methods.UpdatesGetDifferenceAsync(new UpdatesGetDifferenceArgs
            {
                Date = _mutableSettings.Date,
                Pts = _mutableSettings.Pts,
                Qts = _mutableSettings.Qts
            }));

            var empty = difference as UpdatesDifferenceEmpty;
            var diff = difference as UpdatesDifference;
            var slice = difference as UpdatesDifferenceSlice;

            Action dispatchUpdates = () =>
            {
                var updates = new List<object>();
                //TODO: encrypyed messages
                if (diff != null)
                {
                    updates.AddRange(diff.NewMessages);
                    updates.AddRange(diff.OtherUpdates);
                }
                else if(slice!=null)
                {
                    updates.AddRange(slice.NewMessages);
                    updates.AddRange(slice.OtherUpdates);
                }
                ProcessIncomingPayload(updates, false, client);
            };

            if (diff != null)
            {
				_dialogs.AddUsers(diff.Users);
				_dialogs.AddChats(diff.Chats);
                dispatchUpdates();
                var state = (UpdatesState)diff.State;
				SaveState(state.Date, state.Pts, state.Qts, state.Seq);
            }
            else if (slice != null)
            {
				_dialogs.AddUsers(slice.Users);
				_dialogs.AddChats(slice.Chats);
                dispatchUpdates();
                var state = (UpdatesState)slice.IntermediateState;
				SaveState(state.Date, state.Pts, state.Qts, state.Seq);
                counter++;
                goto Again;
            }
            else if (empty != null)	
            {
                SaveState(empty.Date, 0, 0, empty.Seq);
            }
        }

        private void FetchChannelDifference(TelegramClient client)
        {
            var extendedBubbleGroups = BubbleGroupManager.FindAll(this).Where(group => group.IsExtendedParty);
            foreach (var bubbleGroup in extendedBubbleGroups)
            {
                try
                {
                Again:
                    var channelAddress = uint.Parse(bubbleGroup.Address);
                    var channel = _dialogs.GetChat(uint.Parse(bubbleGroup.Address));
                    var channelObj = channel as Channel;
                    if (channelObj != null)
                    {
                        // do-nothing
                    }
                    else
                    {
                        Utils.DebugPrint("### There is no channel in the database, reconstructing it from server");
                        FetchNewDialogs(client);
                    }
                    var channelPts = _dialogs.GetChatPts(channelAddress);
                    if (channelPts == 0)
                    {
                        uint pts = GetLastPtsForChannel(client, channelAddress.ToString());
                        SaveChannelState(channelAddress, pts);
                    }
                    var result = TelegramUtils.RunSynchronously(
                        client.Methods.UpdatesGetChannelDifferenceAsync(new UpdatesGetChannelDifferenceArgs
                        {
                            Channel = new InputChannel
                            {
                                ChannelId = channelAddress,
                                AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(channelAddress))
                            },
                            Filter = new ChannelMessagesFilterEmpty(),
                            Limit = 100,
                            Pts = channelPts
                        }));
                    var updates = ProcessChannelDifferenceResult(channelAddress, result);
                    if (updates.Any())
                    {
                        ProcessIncomingPayload(updates, false, client);
                        goto Again;
                    }
                }
                catch (Exception e)
                {
                    DebugPrint("#### Exception getting channels" + e);
                    continue;
                }
            }
        }

        private uint GetLastMessageIdService(BubbleGroup bubbleGroup)
        {
            var id = bubbleGroup.LastBubbleSafe().IdService;
            if (id == null)
            {
                return 0;
            }
            return uint.Parse(id);
        }

        private List<object> ProcessChannelDifferenceResult(uint channelId, IUpdatesChannelDifference result)
        {
            var updatesChannelDifference = result as UpdatesChannelDifference;
            var updatesChannelDifferenceEmpty = result as UpdatesChannelDifferenceEmpty;
            var updatesChannelDifferenceTooLong = result as UpdatesChannelDifferenceTooLong;

            var updatesList = new List<object>();

            if (updatesChannelDifference != null)
			{
                updatesList.AddRange(updatesChannelDifference.OtherUpdates);
                updatesList.AddRange(updatesChannelDifference.Chats);
				updatesList.AddRange(updatesChannelDifference.NewMessages);
                SaveChannelState(channelId, updatesChannelDifference.Pts);
            }
            else if (updatesChannelDifferenceEmpty != null)
            {
                SaveChannelState(channelId, updatesChannelDifferenceEmpty.Pts);
            }
            else if (updatesChannelDifferenceTooLong != null)
            {
				updatesList.AddRange(updatesChannelDifferenceTooLong.Users);
				updatesList.AddRange(updatesChannelDifferenceTooLong.Chats);
				updatesList.AddRange(updatesChannelDifferenceTooLong.Messages);
                SaveChannelState(channelId, updatesChannelDifferenceTooLong.Pts);
            }
            return updatesList;
        }

        private void FetchState(TelegramClient client)
        {
            if (_mutableSettings.Date == 0)
            {
                DebugPrint("We need to fetch the state!");
                var state = (UpdatesState)TelegramUtils.RunSynchronously(client.Methods.UpdatesGetStateAsync(new UpdatesGetStateArgs()));
                SaveState(state.Date, state.Pts, state.Qts, state.Seq);
            }
            else
            {
                FetchDifference(client);
            }
        }

        public override void Connect(WakeLock wakeLock)
        {
            Disconnect();
            var sessionId = GetRandomId();
            var transportConfig = 
                new TcpClientTransportConfig(_settings.NearestDcIp, _settings.NearestDcPort);
            using (var client = new TelegramClient(transportConfig, 
                                    new ConnectionConfig(_settings.AuthKey, _settings.Salt), AppInfo))
            {
				try
				{
					var result = TelegramUtils.RunSynchronously(client.Connect());
					if (result != MTProtoConnectResult.Success)
					{
						throw new Exception("Failed to connect: " + result);
					}
				
	                DebugPrint("Registering long poller...");
	                var registerDeviceResult = TelegramUtils.RunSynchronously(client.Methods.AccountRegisterDeviceAsync(
	                    new AccountRegisterDeviceArgs
	                {
	                    TokenType = 7,
	                    Token = sessionId.ToString(CultureInfo.InvariantCulture),
	                }));
	                if (!registerDeviceResult)
	                {
	                    throw new Exception("Failed to register long poller...");
	                }
				}
				catch (Exception ex)
				{
					Utils.DebugPrint("Exception while connecting telegram! " + ex);
					if (ex.Message != null && ex.Message.Contains("401"))
					{
						ResetTelegram();
					}
					throw ex;
				}


				DebugPrint(">>>>>>>>>>>>>> Fetching state!");
                FetchState(client);
                DebugPrint (">>>>>>>>>>>>>> Fetching dialogs!");
                GetDialogs(client);
                GetConfig(client);
            }

            DebugPrint("Starting long poller...");
            if (_longPollClient != null)
            {
                _longPollClient.OnUpdateTooLong -= OnLongPollClientUpdateTooLong;
                _longPollClient.OnClosedInternally -= OnLongPollClientClosed;
            }
            _longPollerAborted = false;
            _longPollClient = new TelegramClient(transportConfig, 
                new ConnectionConfig(_settings.AuthKey, _settings.Salt) { SessionId = sessionId }, AppInfo);
            var result2 = TelegramUtils.RunSynchronously(_longPollClient.Connect());
            if (result2 != MTProtoConnectResult.Success)
            {
                throw new Exception("Failed to connect long poll client: " + result2);
            } 
            _longPollClient.OnUpdateTooLong += OnLongPollClientUpdateTooLong;
            _longPollClient.OnClosedInternally += OnLongPollClientClosed;
           	ScheduleLongPollPing();
            DebugPrint("Long poller started!");
        }

		private void ResetTelegram()
		{
			SettingsManager.Delete(this);
		}

		public override void Disconnect()
        {
            DisconnectFullClientIfPossible();
            DisconnectLongPollerIfPossible();
        }

        public override string GetIcon(bool large)
        {
            if (large)
            {
                return Constants.LargeIcon;
            }

            return Constants.SmallIcon;
        }

        public override IEnumerable<Bubble> ProcessBubbles()
        {
            throw new NotImplementedException();
        }

        public override void SendBubble(Bubble b)
        {
            var presenceBubble = b as PresenceBubble;
            if (presenceBubble != null)
            {
                _hasPresence = presenceBubble.Available;
                SetFullClientPingDelayDisconnect();
                using (var client = new FullClientDisposable(this))
                {
                    if (_hasPresence)
                    {
                        if (_mutableSettings.Date != 0)
                        {
                            // as thy come forth, thy shall get difference
                            FetchDifference(client.Client);
                        }
                        var updatedUsers = GetUpdatedUsersOfAllDialogs(client.Client);
                        if (updatedUsers != null)
                        {
                            foreach (var updatedUser in updatedUsers)
                            {
                                TelegramEventBubble(new PresenceBubble(Time.GetNowUnixTimestamp(), Bubble.BubbleDirection.Incoming,
                                    TelegramUtils.GetUserId(updatedUser), false, this, TelegramUtils.GetAvailable(updatedUser)));
                            }
                        }
                    }
                    SendPresence(client.Client);
                }
            }

            var typingBubble = b as TypingBubble;
            if (typingBubble != null)
            {
                var peer = GetInputPeer(typingBubble.Address, typingBubble.Party, typingBubble.ExtendedParty);
                using (var client = new FullClientDisposable(this))
                {
                    TelegramUtils.RunSynchronously(client.Client.Methods.MessagesSetTypingAsync(
                        new MessagesSetTypingArgs
                        {
                            Peer = peer,
                            Action = typingBubble.IsAudio ? (ISendMessageAction)new SendMessageRecordAudioAction() : (ISendMessageAction)new SendMessageTypingAction()
                        }));
                }
            }

            var readBubble = b as ReadBubble;
            if (readBubble != null)
            {
                using (var client = new FullClientDisposable(this))
                {
                    var peer = GetInputPeer(readBubble.Address, readBubble.Party, readBubble.ExtendedParty);
                    if (peer is InputPeerChannel)
                    {
                        TelegramUtils.RunSynchronously(client.Client.Methods.ChannelsReadHistoryAsync(new ChannelsReadHistoryArgs
                        {
                            Channel = new InputChannel
                            {
                                ChannelId = uint.Parse(readBubble.Address),
                                AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(uint.Parse(readBubble.Address)))
                            },
                            MaxId = 0
                        }));
                    }
                    else
                    {
                        var messagesAffectedMessages =
                            TelegramUtils.RunSynchronously(client.Client.Methods.MessagesReadHistoryAsync(
                            new MessagesReadHistoryArgs
                            {
                                Peer = peer,
                                MaxId = 0,

                            })) as MessagesAffectedMessages;
                        //if (messagesAffectedMessages != null)
                        //{
                        //    SaveState(0, messagesAffectedMessages.Pts, 0, 0);
                        //}
                    }
                }
            }

            try
            {
                var textBubble = b as TextBubble;
                if (textBubble != null)
                {
                    var peer = GetInputPeer(textBubble.Address, textBubble.Party, textBubble.ExtendedParty);

                    MessagesSendMessageArgs sendMessageArgs = null;
                    MessagesSendInlineBotResultArgs sendInlineBotResultArgs = null;
                    var isSendInlineBotResult = textBubble.BotInlineResult != null;

                    if (isSendInlineBotResult)
                    {
                        // Bot Inline Mode is sending a message
                        sendInlineBotResultArgs = new MessagesSendInlineBotResultArgs
                        {
                            Flags = 0,
                            Peer = peer,
                            RandomId = ulong.Parse(textBubble.IdService2),
                            QueryId = ulong.Parse(textBubble.BotInlineResult.QueryId),
                            Id = textBubble.BotInlineResult.Id
                        };

                        // Adjust for quote if necessary
                        if (!string.IsNullOrEmpty(textBubble.QuotedIdService))
                        {
                            sendInlineBotResultArgs.Flags |= MESSAGE_FLAG_REPLY;
                            sendInlineBotResultArgs.ReplyToMsgId = uint.Parse(textBubble.QuotedIdService);
                        }
                    }
                    else
                    {
                        // Standard send message
                        sendMessageArgs = new MessagesSendMessageArgs
                        {
                            Flags = 0,
                            Peer = peer,
                            Message = textBubble.Message,
                            RandomId = ulong.Parse(textBubble.IdService2),
                        };

                        // Adjust for bubble markup if necessary
                        if (textBubble.BubbleMarkups != null &&
                            textBubble.BubbleMarkups.Count > 0)
                        {
                            sendMessageArgs.Entities = HandleBubbleMarkup(textBubble.BubbleMarkups);
                            if (sendMessageArgs.Entities != null)
                            {
                                sendMessageArgs.Flags |= MESSAGE_FLAG_ENTITIES;
                            }
                        }

                        // Adjust for quote if necessary
                        if (!string.IsNullOrEmpty(textBubble.QuotedIdService))
                        {
                            sendMessageArgs.Flags |= MESSAGE_FLAG_REPLY;
                            sendMessageArgs.ReplyToMsgId = uint.Parse(textBubble.QuotedIdService);
                        }

                        // Adjust for keyboard if any (Bots Inline Mode)
                        if (textBubble.KeyboardMarkup != null)
                        {
                            var keyboardInlineMarkup = textBubble.KeyboardMarkup as Bots.KeyboardInlineMarkup;
                            if (keyboardInlineMarkup != null)
                            {
                                sendMessageArgs.Flags |= MESSAGE_FLAG_HAS_MARKUP;
                                sendMessageArgs.ReplyMarkup = HandleKeyboardInlineMarkup(keyboardInlineMarkup);
                            }
                        }
                    }

                    using (var client = new FullClientDisposable(this))
                    {
                        IUpdates iUpdates = null;
                        if (isSendInlineBotResult)
                        {
                            iUpdates = TelegramUtils.RunSynchronously(
                                client.Client.Methods.MessagesSendInlineBotResultAsync(sendInlineBotResultArgs));
                        }
                        else
                        {
                            iUpdates = TelegramUtils.RunSynchronously(
                                client.Client.Methods.MessagesSendMessageAsync(sendMessageArgs));
                        }

                        var updateShortSentMessage = iUpdates as UpdateShortSentMessage;
                        if (updateShortSentMessage != null)
                        {
                            textBubble.IdService = updateShortSentMessage.Id.ToString(CultureInfo.InvariantCulture);
                        }
                        var updates = iUpdates as Updates;
                        if (updates != null)
                        {
                            foreach (var update in updates.UpdatesProperty)
                            {
                                var updateMessageId = update as UpdateMessageID;
                                if (updateMessageId != null)
                                {
                                    textBubble.IdService = updateMessageId.Id.ToString(CultureInfo.InvariantCulture);
                                    break;
                                }
                                var updateNewChannelMessage = update as UpdateNewChannelMessage;
                                if (updateNewChannelMessage == null)
                                    continue;
                                var message = updateNewChannelMessage.Message as Message;
                                if (message != null)
                                {
                                    textBubble.IdService = message.Id.ToString(CultureInfo.InvariantCulture);
                                    break;
                                }
                            }
                        }
                        SendToResponseDispatcher(iUpdates, client.Client);
                    }
                }

                var imageBubble = b as ImageBubble;
                if (imageBubble != null)
                {
                    var fileId = GenerateRandomId();
                    var inputFile = UploadFile(imageBubble, fileId, 0);
                    SendFile(imageBubble, inputFile);
                }

                var fileBubble = b as FileBubble;
                if (fileBubble != null)
                {
                    var fileId = GenerateRandomId();
                    var fileInfo = new FileInfo(fileBubble.Path);
                    DebugPrint(">>>>>>> the size of the file is " + fileInfo.Length);
                    if (fileInfo.Length <= 10485760)
                    {
                        var inputFile = UploadFile(fileBubble, fileId, fileInfo.Length);
                        SendFile(fileBubble, inputFile);
                    }
                    else
                    {
                        var inputFile = UploadBigFile(fileBubble, fileId, fileInfo.Length);
                        SendFile(fileBubble, inputFile);
                    }
                }

                var audioBubble = b as AudioBubble;
                if (audioBubble != null)
                {
                    var fileId = GenerateRandomId();
                    var fileInfo = new FileInfo(audioBubble.AudioPath);
                    DebugPrint(">>>>>>> the size of the file is " + fileInfo.Length);
                    if (fileInfo.Length <= 10485760)
                    {
                        var inputFile = UploadFile(audioBubble, fileId, fileInfo.Length);
                        SendFile(audioBubble, inputFile);
                    }
                    else
                    {
                        var inputFile = UploadBigFile(audioBubble, fileId, fileInfo.Length);
                        SendFile(audioBubble, inputFile);
                    }
                }

                var locationBubble = b as LocationBubble;
                if (locationBubble != null)
                {
                    SendGeoLocation(locationBubble);
                }

                var contactBubble = b as ContactBubble;
                if (contactBubble != null)
                {
                    SendContact(contactBubble);
                }

                var stickerBubble = b as StickerBubble;
                if (stickerBubble != null)
                {
                    var fileId = GenerateRandomId();
                    var inputFile = UploadFile(stickerBubble, fileId, 0);
                    SendFile(stickerBubble, inputFile);
                }

            }
            catch (Exception ex)
            {
                throw new ServiceQueueBubbleException("Failed to send message: " + ex);
            }
        }

        private void SendContact(ContactBubble contactBubble)
        {
            var inputPeer = GetInputPeer(contactBubble.Address, contactBubble.Party, contactBubble.ExtendedParty);
            var contactCard = Platform.GenerateContactCardFromBytes(contactBubble.VCardData);
            if (contactCard != null)
            {
                // Standard send contact
                var args = new MessagesSendMediaArgs
                {
                    Flags = 0,
                    Peer = inputPeer,
                    Media = new InputMediaContact
                    {
                        FirstName = contactCard.GivenName,
                        LastName = contactCard.FamilyName,
                        PhoneNumber = contactCard.Phones?.FirstOrDefault()?.Number
                    },
                    RandomId = GenerateRandomId(),
                };

                // Adjust for quote if necessary
                if (!string.IsNullOrEmpty(contactBubble.QuotedIdService))
                {
                    args.Flags |= MESSAGE_FLAG_REPLY;
                    args.ReplyToMsgId = uint.Parse(contactBubble.QuotedIdService);
                }

                // Adjust for keyboard if any (Bots Inline Mode)
                if (contactBubble.KeyboardMarkup != null)
                {
                    var keyboardInlineMarkup = contactBubble.KeyboardMarkup as Bots.KeyboardInlineMarkup;
                    if (keyboardInlineMarkup != null)
                    {
                        args.Flags |= MESSAGE_FLAG_HAS_MARKUP;
                        args.ReplyMarkup = HandleKeyboardInlineMarkup(keyboardInlineMarkup);
                    }
                }

                using (var client = new FullClientDisposable(this))
                {
                    var updates = TelegramUtils.RunSynchronously(
                        client.Client.Methods.MessagesSendMediaAsync(args));
                    var id = GetMessageId(updates);
                    contactBubble.IdService = id;
                    SendToResponseDispatcher(updates, client.Client);
                }
            }
        }

        private void SendGeoLocation(LocationBubble locationBubble)
        {
            var inputPeer = GetInputPeer(locationBubble.Address, locationBubble.Party, locationBubble.ExtendedParty);

            // Standard send location
            var args = new MessagesSendMediaArgs
            {
                Flags = 0,
                Peer = inputPeer,
                Media = new InputMediaGeoPoint
                {
                    GeoPoint = new InputGeoPoint
                    {
                        Lat = locationBubble.Latitude,
                        Long = locationBubble.Longitude
                    }
                },
                RandomId = GenerateRandomId(),
            };

            // Adjust for quote if necessary
            if (!string.IsNullOrEmpty(locationBubble.QuotedIdService))
            {
                args.Flags |= MESSAGE_FLAG_REPLY;
                args.ReplyToMsgId = uint.Parse(locationBubble.QuotedIdService);
            }

            // Adjust for keyboard if any (Bots Inline Mode)
            if (locationBubble.KeyboardMarkup != null)
            {
                var keyboardInlineMarkup = locationBubble.KeyboardMarkup as Bots.KeyboardInlineMarkup;
                if (keyboardInlineMarkup != null)
                {
                    args.Flags |= MESSAGE_FLAG_HAS_MARKUP;
                    args.ReplyMarkup = HandleKeyboardInlineMarkup(keyboardInlineMarkup);
                }
            }

            using (var client = new FullClientDisposable(this))
            {
                var updates = TelegramUtils.RunSynchronously(
                    client.Client.Methods.MessagesSendMediaAsync(args));
                var id = GetMessageId(updates);
                locationBubble.IdService = id;
                SendToResponseDispatcher(updates,client.Client);
            }
        }

        private IInputFile UploadBigFile(VisualBubble bubble, ulong fileId, long fileSize)
        {
            uint chunkSize = 524288;
            var fileTotalParts = (uint)fileSize/chunkSize;
            if (fileSize % 1024 != 0)
            {
                fileTotalParts++; 
            }
            var chunk = new byte[chunkSize];
            uint chunkNumber = 0;
            var offset = 0;
            using (var file = File.OpenRead(GetPathFromBubble(bubble)))
            {
                using (var client = new FullClientDisposable(this))
                {
                    int bytesRead;
                    while ((bytesRead = file.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        var uploaded =
                            TelegramUtils.RunSynchronously(
                                client.Client.Methods.UploadSaveBigFilePartAsync(new UploadSaveBigFilePartArgs
                                {
                                    Bytes = chunk,
                                    FileId = fileId,
                                    FilePart = chunkNumber,
                                    FileTotalParts = fileTotalParts,
                                }));
                        if (!uploaded)
                        {
                            throw new Exception("The file chunk failed to be uploaded");
                        }
                        chunkNumber++;
                        offset += bytesRead;
                        UpdateSendProgress(bubble, offset, fileSize);
                    }
                    return new InputFileBig
                    {
                        Id = fileId,
                        Name = GetNameFromBubble(bubble),
                        Parts = chunkNumber
                    };
                }
            }

        }

        private int CalculateChunkSize(long fileSize)
        {
            var uploadChunkSize = 32*1024;
            while ((fileSize / (int)uploadChunkSize) > 1000)
            {
                uploadChunkSize *= 2;
            }
            return uploadChunkSize;
        }

        private string GetNameFromBubble(VisualBubble bubble)
        {
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;
            var imageBubble = bubble as ImageBubble;
            var stickerBubble = bubble as StickerBubble;

            if (fileBubble != null)
            {
                return fileBubble.FileName;
            }
            else if (audioBubble != null)
            {
                return audioBubble.AudioPathNative;
            }
            else if(imageBubble!=null)
            {
                return imageBubble.ImagePathNative;
            }
            else if (stickerBubble != null)
            {
                return stickerBubble.StickerPathNative;
            }
            return null;

        }

        private string GetPathFromBubble(VisualBubble bubble)
        {
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;
            var imageBubble = bubble as ImageBubble;
            var stickerBubble = bubble as StickerBubble;

            if (fileBubble != null)
            {
                return fileBubble.Path;
            }
            else if (audioBubble != null)
            {
                return audioBubble.AudioPath;
            }
            else if(imageBubble!=null)
            {
                return imageBubble.ImagePath;
            }
            else if (stickerBubble != null)
            {
                return stickerBubble.StickerPath;
            }
            return null;

        }

        private IInputFile UploadFile(VisualBubble bubble, ulong fileId, long fileSize)
        {
            int chunkSize = CalculateChunkSize(fileSize);
            var chunk = new byte[chunkSize];
            uint chunkNumber = 0;
            var offset = 0;
            using (var file = File.OpenRead(GetPathFromBubble(bubble)))
            {
                using (var client = new FullClientDisposable(this))
                {
                    int bytesRead;
                    while ((bytesRead = file.Read(chunk, 0, chunk.Length)) > 0)
                    {
						//RPC call
						try
						{
							var uploaded =
								TelegramUtils.RunSynchronously(
									client.Client.Methods.UploadSaveFilePartAsync(new UploadSaveFilePartArgs
									{
										Bytes = chunk,
										FileId = fileId,
										FilePart = chunkNumber
									}));

							if (!uploaded)
							{
								throw new Exception("The file chunk failed to be uploaded");
							}
							chunkNumber++;
							offset += bytesRead;
							UpdateSendProgress(bubble, offset, fileSize);
						}
						catch (Exception ex)
						{
							Utils.DebugPrint("Exception while uploading file " + ex.InnerException);
							throw ex;
						}
                    }
                    return new InputFile
                    {
                        Id = fileId,
                        Md5Checksum = "",
                        Name = GetNameFromBubble(bubble),
                        Parts = chunkNumber
                    };
                    
                }
            }
        }

        private void UpdateSendProgress(VisualBubble bubble, int offset, long fileSize)
        {
            if (fileSize == 0)
            {
                return;
            }
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;
            if(fileBubble!=null)
            {
                float progress = offset / (float)fileSize;
                if (fileBubble.Transfer != null && fileBubble.Transfer.Progress != null)
                {
                    try
                    {
                        fileBubble.Transfer.Progress((int) (progress*100));
                    }
                    catch (Exception ex)
                    {
                    }
                }

            }else if (audioBubble != null)
            {
                float progress = offset / (float)fileSize;
                if (audioBubble.Transfer != null && audioBubble.Transfer.Progress != null)
                {
                    try
                    {
                        audioBubble.Transfer.Progress((int)(progress * 100));
                    }
                    catch (Exception ex)
                    {
                    }
                }
            }
        }

        private string GetMessageId(IUpdates iUpdates)
        {
            var updates = iUpdates as Updates;
            if (updates != null)
            {
                foreach (var update in updates.UpdatesProperty)
                {
                    var updateNewMessage = update as UpdateNewMessage;
                    var updateNewChannelMessage = update as UpdateNewChannelMessage;
                    var updateMessageId = update as UpdateMessageID;
                    if (updateNewMessage != null)
                    {
                        var message = updateNewMessage.Message as Message;
                        return message?.Id.ToString(CultureInfo.InvariantCulture);
                    }
                    if (updateNewChannelMessage != null)
                    {
                        var message = updateNewChannelMessage.Message as Message;
                        return message?.Id.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            return null;
        }

        private void SendFile(VisualBubble bubble, IInputFile inputFile)
        {
            var inputPeer = GetInputPeer(bubble.Address, bubble.Party, bubble.ExtendedParty);
            var imageBubble = bubble as ImageBubble;
            var fileBubble = bubble as FileBubble;
            var audioBubble = bubble as AudioBubble;
            var stickerBubble = bubble as StickerBubble;

            using (var client = new FullClientDisposable(this))
            {
                Action<VisualBubble, string, string> dispatchFile = 
                    (visualBubble, fileName, mimeType) =>
                {
					var documentAttributes = new List<IDocumentAttribute>
					{
						new SharpTelegram.Schema.DocumentAttributeFilename
						{
							FileName = fileName
						}
					};

					// Standard file sent
					var args = new MessagesSendMediaArgs
					{
						Flags = 0,
						Peer = inputPeer,
						Media = new InputMediaUploadedDocument
						{
							Attributes = documentAttributes,
							Caption = "",
							File = inputFile,
							MimeType = mimeType,
						},
						RandomId = GenerateRandomId(),
					};

					// Adjust for quote if necessary
					if (!string.IsNullOrEmpty(visualBubble.QuotedIdService))
					{
						args.Flags |= MESSAGE_FLAG_REPLY;
						args.ReplyToMsgId = uint.Parse(visualBubble.QuotedIdService);
					}

					// Adjust for keyboard if any (Bots Inline Mode)
					if (visualBubble.KeyboardMarkup != null)
					{
						var keyboardInlineMarkup = visualBubble.KeyboardMarkup as Bots.KeyboardInlineMarkup;
						if (keyboardInlineMarkup != null)
						{
							args.Flags |= MESSAGE_FLAG_HAS_MARKUP;
							args.ReplyMarkup = HandleKeyboardInlineMarkup(keyboardInlineMarkup);
						}
					}

					var iUpdates = TelegramUtils.RunSynchronously(
						client.Client.Methods.MessagesSendMediaAsync(args));
					var messageId = GetMessageId(iUpdates);
					visualBubble.IdService = messageId;
					SendToResponseDispatcher(iUpdates, client.Client);
                };

                if (imageBubble != null)
                {
                    if (!imageBubble.IsAnimated)
                    {
						// Standard send image
						var args = new MessagesSendMediaArgs
						{
							Flags = 0,
							Peer = inputPeer,
							Media = new InputMediaUploadedPhoto
							{
								Caption = "",
								File = inputFile,
							},
							RandomId = GenerateRandomId(),
						};

						// Adjust for quote if necessary
						if (!string.IsNullOrEmpty(imageBubble.QuotedIdService))
						{
							args.Flags |= MESSAGE_FLAG_REPLY;
							args.ReplyToMsgId = uint.Parse(imageBubble.QuotedIdService);
						}

						// Adjust for keyboard if any (Bots Inline Mode)
						if (imageBubble.KeyboardMarkup != null)
						{
							var keyboardInlineMarkup = imageBubble.KeyboardMarkup as Bots.KeyboardInlineMarkup;
							if (keyboardInlineMarkup != null)
							{
								args.Flags |= MESSAGE_FLAG_HAS_MARKUP;
								args.ReplyMarkup = HandleKeyboardInlineMarkup(keyboardInlineMarkup);
							}
						}

						var iUpdates = TelegramUtils.RunSynchronously(
							client.Client.Methods.MessagesSendMediaAsync(args));
						var messageId = GetMessageId(iUpdates);
						imageBubble.IdService = messageId;
						var updates = iUpdates as Updates;

						SendToResponseDispatcher(iUpdates, client.Client);
                    }
                    else
                    {
                        dispatchFile(imageBubble, Path.GetFileName(imageBubble.ImagePath), "image/gif");
                    }
                }
                else if (stickerBubble != null)
                {
                    dispatchFile(stickerBubble, Path.GetFileName(stickerBubble.StickerPath), "image/webp");
                }
                else if (fileBubble != null)
                {
                    dispatchFile(fileBubble, fileBubble.FileName, fileBubble.MimeType);
                }
                else if (audioBubble != null)
                {
                    var documentAttributes = new List<IDocumentAttribute>();

                    if (audioBubble.Recording)
                    {
                        documentAttributes.Add(new SharpTelegram.Schema.DocumentAttributeAudio
                        {
                            Flags = 1024,
                            Duration = (uint)audioBubble.Seconds,
                            Voice = new True()
                        });
                    }
                    else if (audioBubble.FileName != null)
                    {
                        documentAttributes.Add(new SharpTelegram.Schema.DocumentAttributeAudio
                        {
                            Flags = 1,
                            Duration = (uint)audioBubble.Seconds,
                            Title = audioBubble.FileName
                        });
                    }
                    else 
                    {
                        documentAttributes.Add(new SharpTelegram.Schema.DocumentAttributeAudio
                        {
                            Flags = 0,
                            Duration = (uint)audioBubble.Seconds
                        });
                    }

                    var mimeType = Platform.GetMimeTypeFromPath(audioBubble.AudioPath);
                    var inputMedia = new InputMediaUploadedDocument
                    {
                        Attributes = documentAttributes,
                        Caption = "",
                        File = inputFile,
                        MimeType = mimeType,
                    };

                    var media = new MessagesSendMediaArgs
                    {
                        Flags = 0,
                        Media = inputMedia,
                        Peer = inputPeer,
                        RandomId = GenerateRandomId(),
                    };

                    // Adjust for quote if necessary
                    if (!string.IsNullOrEmpty(audioBubble.QuotedIdService))
                    {
                        media.Flags |= MESSAGE_FLAG_REPLY;
                        media.ReplyToMsgId = uint.Parse(audioBubble.QuotedIdService);
                    }

                    // Adjust for keyboard if any (Bots Inline Mode)
                    if (audioBubble.KeyboardMarkup != null)
                    {
                        var keyboardInlineMarkup = audioBubble.KeyboardMarkup as Bots.KeyboardInlineMarkup;
                        if (keyboardInlineMarkup != null)
                        {
                            media.Flags |= MESSAGE_FLAG_HAS_MARKUP;
                            media.ReplyMarkup = HandleKeyboardInlineMarkup(keyboardInlineMarkup);
                        }
                    }

                    var iUpdates = TelegramUtils.RunSynchronously(
                        client.Client.Methods.MessagesSendMediaAsync(media));
                    var messageId = GetMessageId(iUpdates);
                    audioBubble.IdService = messageId;
                    SendToResponseDispatcher(iUpdates, client.Client);
                }
            }
        }


        public ulong GenerateRandomId()
        {
            byte[] buffer = new byte[sizeof(UInt64)];
            var random = new Random();
            random.NextBytes(buffer);
            var id = BitConverter.ToUInt64(buffer, 0);
            return id;
        }

        void SendToResponseDispatcher(IUpdates iUpdate,TelegramClient client)
        {
                var mtProtoClientConnection = client.Connection as MTProtoClientConnection;
                if (mtProtoClientConnection != null)
                {
                    var responseDispatcher = mtProtoClientConnection.ResponseDispatcher as ResponseDispatcher;
                    if (responseDispatcher != null)
                    {
                        SharpMTProto.Schema.IMessage tempMessage = new SharpMTProto.Schema.Message(0,0,iUpdate);
                        responseDispatcher.DispatchAsync(tempMessage).Wait();
                    }
                }
        }

        private IInputPeer GetInputPeer(string id, bool groupChat, bool superGroup)
        {
            if (superGroup)
            {
                return new InputPeerChannel
                {
                    ChannelId = uint.Parse(id),
                    AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(uint.Parse(id)))
                };
            }
            if (groupChat)
            {
                return new InputPeerChat
                {
                    ChatId = uint.Parse(id)
                };
            }
            var accessHash = GetUserAccessHashIfForeign(id);
            return new InputPeerUser
            {
                UserId = uint.Parse(id),
                AccessHash = accessHash
            };
        }

        private ulong GetUserAccessHashIfForeign(string userId)
        { 
            var user = _dialogs.GetUser(uint.Parse(userId));
            if (user != null)
            {
                return TelegramUtils.GetAccessHash(user);
            }
            return 0;
        }

        public override bool BubbleGroupComparer(string first, string second)
        {
            return first == second;
        }

        public override Task GetBubbleGroupLegibleId(BubbleGroup group, Action<string> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(null);
            });
        }

        private async Task<List<IUser>> GetUsers(List<IInputUser> users, TelegramClient client)
        {
            var response = await client.Methods.UsersGetUsersAsync(new UsersGetUsersArgs
            {
                Id = users
            });
            return response;
        }

        private async Task<IUser> GetUser(IInputUser user, TelegramClient client)
        {
            return (await GetUsers(new List<IInputUser> { user }, client)).First();
        }

        private async Task<List<User>> FetchContacts()
        {
            if (contactsCache.Count != 0)
            {
                return contactsCache;
            }
            using (var client = new FullClientDisposable(this))
            {
                var response = (ContactsContacts)await client.Client.Methods.ContactsGetContactsAsync(
                    new ContactsGetContactsArgs
                    {
                        Hash = string.Empty
                    });
                contactsCache.AddRange(response.Users.OfType<User>().ToList());
                _dialogs.AddUsers(response.Users);
                return contactsCache;
            }

        }

        public override Task GetBubbleGroupName(BubbleGroup group, Action<string> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(GetTitle(group.Address, group.IsParty));
            });
        }

        public override Task GetBubbleGroupPhoto(BubbleGroup group, Action<DisaThumbnail> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(GetThumbnail(group.Address, group.IsParty, true));
            });
        }

        public override Task GetBubbleGroupPartyParticipants(BubbleGroup group, Action<DisaParticipant[]> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(null);
            });
        }

        public override Task GetBubbleGroupUnknownPartyParticipant(BubbleGroup group, string unknownPartyParticipant, Action<DisaParticipant> result)
        {
            return Task.Factory.StartNew(() =>
            {
                var name = GetUserNameAndHandle(unknownPartyParticipant);
                if (name != null)
                {
                    result(new DisaParticipant
                    {
                        Name = name.Item1,
                        Username = name.Item2,
                        Address = unknownPartyParticipant
                    });
                }
                else
                {
                    result(null);
                }
            });
        }

        public override Task GetBubbleGroupPartyParticipantPhoto(DisaParticipant participant, Action<DisaThumbnail> result)
        {
            return Task.Factory.StartNew(() =>
            {
                result(GetThumbnail(participant.Address, false, true));
            });
        }

		public override Task GetBubbleGroupLastOnline(BubbleGroup group, Action<long> result)
		{
			return Task.Factory.StartNew(() =>
			{
				result(GetUpdatedLastOnline(group.Address));
			});
		}

        public override Task GetBubbleGroupInputDisabled(BubbleGroup group, Action<bool> result)
        {
            return Task.Factory.StartNew(() =>
            {
				// Are we a Telegram Channel?
				var channel = _dialogs.GetChat(uint.Parse(group.Address)) as Channel;
				if (channel == null)
				{
					// Ok, we are either a Disa Solo or Disa Party group
					// so we ARE NOT a Disa Channel
					result(false);
				}
				else
				{
                    bool inputDisabled;
                    if (channel.Megagroup == null)
                    {
                        // Input is disabled if:
                        inputDisabled = channel.Creator == null &&         // We ARE NOT the creator
                                        channel.Editor == null;            // AND we ARE NOT an editor
                    }
                    else
                    {
                        inputDisabled = false;
                    }
					result(inputDisabled);
				}
            });
        }

        public void AddVisualBubbleIdServices(VisualBubble bubble)
        {
            // When sending we will use the IdService2 field to hold our our NextMessageId to use.
            // This will occur during BubbleManager's send flow, before we call this Telegram's send implementation.
            bubble.IdService2 = NextMessageId;
        }

        public bool DisctinctIncomingVisualBubbleIdServices()
        {
            // Telegram requires that the IdService or IdService2 shall be distinct.
            //  See CheckType and VisualBubbleIdComparer for a full picture on Telegram's
            // definition of distinction.
            return true;
        }

        public bool CheckType()
        {
            // When checking for distinction between two VisualBubbles IdService or IdService2,
            // we do not include a check on the VisualBubbles Types.
            // That is, we do not allow an ImageBubble and a TextBubble to have the same IdService or IdService2.
            return false;
        }

        public bool VisualBubbleIdComparer(VisualBubble left, VisualBubble right)
        {
            // Sanity checks
            if (left == null ||
                right == null)
            {
                return false;
            }

            return true;
        }

        public override void RefreshPhoneBookContacts()
        {
            Utils.DebugPrint("Phone book contacts have been updated! Sending information to Telegram servers...");
            var contacts = PhoneBook.PhoneBookContacts;
            var inputContacts = new List<IInputContact>();
            foreach (var contact in contacts)
            {
                foreach (var phoneNumber in contact.PhoneNumbers)
                {
                    var clientId = "0";
                    if (contact.ContactId != null)
                    {
                        clientId = contact.ContactId;
                    }
                    inputContacts.Add(new InputPhoneContact
                    {
                        ClientId = ulong.Parse(clientId),
						Phone = FormatNumber(phoneNumber.Number),
                        FirstName = contact.FirstName,
                        LastName = contact.LastName,
                    });
                }
            }
            if (!inputContacts.Any())
            {
                Utils.DebugPrint("There are no input contacts!");
                return;
            }
            try
            {
                using (var client = new FullClientDisposable(this))
                {
                    var importedContacts = TelegramUtils.RunSynchronously(client.Client.Methods.ContactsImportContactsAsync(
                        new ContactsImportContactsArgs
                        {
                            Contacts = inputContacts,
                            Replace = false,
                        }));
                    var contactsImportedcontacts = importedContacts as ContactsImportedContacts;
                    if (contactsImportedcontacts != null)
                    {
                        _dialogs.AddUsers(contactsImportedcontacts.Users);
                    }
                    //invalidate the cache
                    contactsCache = new List<User>();
                }
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Failed to update contacts: " + ex);
            }
        }

        private IUser GetUser(List<IUser> users, string userId)
        {
            foreach (var user in users)
            {
                var userIdInner = TelegramUtils.GetUserId(user);
                if (userId == userIdInner)
                {
                    return user;
                }
            }
            return null;
        }

        private List<IUser> GetUpdatedUsersOfAllDialogs(TelegramClient client)
        {
            var users = new List<IUser>();
            foreach (var userBubbleGroup in BubbleGroupManager.FindAll(this).Where(x => !x.IsParty))
            {
                var user = _dialogs.GetUser(uint.Parse(userBubbleGroup.Address));
                var inputUser = TelegramUtils.CastUserToInputUser(user);
                var updatedUser = GetUser(inputUser, client).Result;
                _dialogs.AddUser(updatedUser);
                if (user != null)
                {
                    users.Add(updatedUser);
                }
            }

            return users;
        }

        private long GetUpdatedLastOnline(string id)
        {
            var user = _dialogs.GetUser(uint.Parse(id));
            if (user != null)
            {
                return TelegramUtils.GetLastSeenTime(user);
            }
            return 0;
        }

        private Tuple<string, string> GetUserNameAndHandle(string id)
        {
            if (id == null)
            {
                return null;
            }
            var user = _dialogs.GetUser(uint.Parse(id));
            if (user == null)
            {
                return null;
            }
            return Tuple.Create(TelegramUtils.GetUserName(user), 
                TelegramUtils.GetUserHandle(user));
        }

        private string GetTitle(string id, bool group)
        {
            if (id == null)
            {
                return null;
            }
            if (group)
            {
                var chat = _dialogs.GetChat(uint.Parse(id));
                if (chat == null)
                {
                    return null;
                }
                return TelegramUtils.GetChatTitle(chat);
            }
            else
            {
                var user = _dialogs.GetUser(uint.Parse(id));
                if (user == null)
                {
                    return null;
                }
                return TelegramUtils.GetUserName(user);
            }
        }

        private IUser GetUser(string id)
        {
            lock (_quickUserLock)
            {
                try
                {
                    using (var client = new FullClientDisposable(this))
                    {
                        var inputUser = new InputUser();
                        inputUser.UserId = uint.Parse(id);
                        var inputList = new List<IInputUser>();
                        inputList.Add(inputUser);
                        var users =
                            TelegramUtils.RunSynchronously(
                                client.Client.Methods.UsersGetUsersAsync(new UsersGetUsersArgs
                                {
                                    Id = inputList,
                                }));
                        var user = users.FirstOrDefault();
                        return user;
                    }
                }
                catch (Exception e)
                {
                    DebugPrint(">>>>>> exception " + e);
                    return null;
                }
            }
        }

        private void InvalidateThumbnail(string id, bool group, bool small)
        {
            if (id == null)
            {
                return;
            }
            var key = id + group + small;

			DisaThumbnail thumbnail = null;

			_thumbnailCache.TryRemove(key, out thumbnail);

			lock(_cachedThumbnailsLock)
			{
				using (var database = new SqlDatabase<CachedThumbnail>(GetThumbnailDatabasePath()))
				{
					var dbThumbnail = database.Store.Where(x => x.Id == key).FirstOrDefault();
					if (dbThumbnail != null)
					{
						database.Store.Delete(x => x.Id == key);
					}
				}
			}
        }

        private string GetThumbnailDatabasePath()
        {
            if (_thumbnailDatabasePathCached != null)
                return _thumbnailDatabasePathCached;

            var databasePath = Platform.GetDatabasePath();
            if (!Directory.Exists(databasePath))
            {
                Utils.DebugPrint("Creating database directory.");
                Directory.CreateDirectory(databasePath);
            }

            _thumbnailDatabasePathCached = Path.Combine(databasePath, "thumbnailCache.db");
            return _thumbnailDatabasePathCached;
        }


        public DisaThumbnail GetThumbnail(string id, bool group, bool small)
        {
            if (id == null)
            {
                return null;
            }
            var key = id + group + small;


            Func<DisaThumbnail, MemoryStream> convertThumbnailToMemoryStream = thumbnail =>
            {
                MemoryStream memoryStream = new MemoryStream();
                Serializer.Serialize<DisaThumbnail>(memoryStream, thumbnail);
                return memoryStream;
            };


            Func<DisaThumbnail, byte[]> serializeDisaThumbnail = thumbnail =>
            {
                using (var memoryStream = convertThumbnailToMemoryStream(thumbnail))
                {
                    return memoryStream.ToArray();
                }
            };

            Func<DisaThumbnail, DisaThumbnail> cache = thumbnail =>
            {
                if (thumbnail == null)
                {
                    return null;
                }
                lock (_cachedThumbnailsLock)
                {
                    using (var database = new SqlDatabase<CachedThumbnail>(GetThumbnailDatabasePath()))
                    {
                        var bytes = serializeDisaThumbnail(thumbnail);
                        if (bytes == null)
                        {
                            return null;
                        }
                        var cachedThumbnail = new CachedThumbnail
                        {
                            Id = key,
                            ThumbnailBytes = bytes
                        };

                        var dbThumbnail = database.Store.Where(x => x.Id == key).FirstOrDefault();
                        if (dbThumbnail != null)
                        {
                            database.Store.Delete(x => x.Id == key);
                            database.Add(cachedThumbnail);
                        }
                        else
                        {
                            database.Add(cachedThumbnail);
                        }
                    }
                }
				_thumbnailCache[key] = thumbnail;
                return thumbnail;
            };

			if (_thumbnailCache.ContainsKey(key))
				return _thumbnailCache[key];

            lock (_cachedThumbnailsLock)
            {
                using (var database = new SqlDatabase<CachedThumbnail>(GetThumbnailDatabasePath()))
                {
                    Retry:
                    var dbThumbnail = database.Store.Where(x => x.Id == key).FirstOrDefault();
                    if (dbThumbnail != null)
                    {
                        if (dbThumbnail.ThumbnailBytes == null)
                        {
                            return null;
                        }
                        using (var stream = new MemoryStream(dbThumbnail.ThumbnailBytes))
                        {
                            var disaThumbnail = Serializer.Deserialize<DisaThumbnail>(stream);
                            disaThumbnail.Failed = false;
                            if (!File.Exists(disaThumbnail.Location))
                            {
                                DebugPrint("The thumbnail cache was purged! purging the database so that everything is redownloaded");
                                database.DeleteAll();
                                goto Retry;
                            }
							_thumbnailCache[key] = disaThumbnail;
                            return disaThumbnail;
                        }
                    }
                }
            }
            if (group)
            {
                var chat = _dialogs.GetChat(uint.Parse(id));
                if (chat == null)
                {
                    return null;
                }
                var fileLocation = TelegramUtils.GetChatThumbnailLocation(chat, small);
                if (fileLocation == null)
                {
                    return null;
                }
                else
                {
                    if (_thumbnailDownloadingDictionary.ContainsKey(key))
                    {
                        return null; //there is a thread waiting to download the thumbnail 
                    }
                    else
                    {
                        _thumbnailDownloadingDictionary.TryAdd(key, true);
                    }
                    var bytes = FetchFileBytes(fileLocation);
                    bool outValue;
                    if (bytes == null)
                    {
                        _thumbnailDownloadingDictionary.TryRemove(key, out outValue);
                        return null;
                    }
                    else
                    {
                        _thumbnailDownloadingDictionary.TryRemove(key, out outValue);
                        return cache(new DisaThumbnail(this, bytes, key));
                    }
                }
            }
            else
            {
                Func<IUser, DisaThumbnail> getThumbnail = user =>
                {
                    var fileLocation = TelegramUtils.GetUserPhotoLocation(user, small);
                    if (fileLocation == null)
                    {
                        return cache(null);
                    }
                    else
                    {
                        if (_thumbnailDownloadingDictionary.ContainsKey(id))
                        {
                            return null; //there is a thread waiting to download the thumbnail 
                        }
                        else
                        {
                            _thumbnailDownloadingDictionary.TryAdd(id, true);
                        }
                        var bytes = FetchFileBytes(fileLocation);
                        bool outValue;
                        if (bytes == null)
                        {
                            _thumbnailDownloadingDictionary.TryRemove(id, out outValue);
                            return null;
                        }
                        else
                        {
                            _thumbnailDownloadingDictionary.TryRemove(id, out outValue);
                            return cache(new DisaThumbnail(this, bytes, key));
                        }
                    }
                };

                var userImg = _dialogs.GetUser(uint.Parse(id));
                return userImg == null ? null : getThumbnail(userImg);
            }
        }

        private static byte[] FetchFileBytes(TelegramClient client, SharpTelegram.Schema.FileLocation fileLocation)
        {
            try
            {
                var response = (UploadFile)TelegramUtils.RunSynchronously(client.Methods.UploadGetFileAsync(
                    new UploadGetFileArgs
                    {
                        Location = new InputFileLocation
                        {
                            VolumeId = fileLocation.VolumeId,
                            LocalId = fileLocation.LocalId,
                            Secret = fileLocation.Secret
                        },
                        Offset = 0,
                        Limit = uint.MaxValue,
                    }));
                return response.Bytes;
            }
            catch (Exception ex)
            {
                Utils.DebugPrint("Exception while fetching file bytes: " + ex);
                return null;
            }

        }

        private static byte[] FetchFileBytes(TelegramClient client, SharpTelegram.Schema.FileLocation fileLocation, uint offset, uint limit)
        {
            var response = (UploadFile)TelegramUtils.RunSynchronously(client.Methods.UploadGetFileAsync(
                new UploadGetFileArgs
                {
                    Location = new InputFileLocation
                    {
                        VolumeId = fileLocation.VolumeId,
                        LocalId = fileLocation.LocalId,
                        Secret = fileLocation.Secret
                    },
                    Offset = offset,
                    Limit = limit,
                }));
            return response.Bytes;
        }

        private byte[] FetchFileBytes(SharpTelegram.Schema.FileLocation fileLocation)
        {
            if (fileLocation.DcId == _settings.NearestDcId)
            {
                using (var clientDisposable = new FullClientDisposable(this))
                {
                    return FetchFileBytes(clientDisposable.Client, fileLocation);
                }   
            }
            else
            {
                try
                {
                    var client = GetClient((int)fileLocation.DcId);
                    return FetchFileBytes(client, fileLocation);
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to obtain client from DC manager: " + ex);
                    return null;
                }
            }
        }


        private byte[] FetchFileBytes(SharpTelegram.Schema.FileLocation fileLocation,uint offset,uint limit)
        {
            if (fileLocation.DcId == _settings.NearestDcId)
            {
                using (var clientDisposable = new FullClientDisposable(this))
                {
                    return FetchFileBytes(clientDisposable.Client, fileLocation,offset,limit);
                }
            }
            else
            {
                try
                {
                    var client = GetClient((int)fileLocation.DcId);
                    return FetchFileBytes(client, fileLocation,offset,limit);
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to obtain client from DC manager: " + ex);
                    return null;
                }
            }
        }

        private void GetConfig(TelegramClient client)
        {
			DebugPrint (">>>>>>>>>>>>>> Getting config!");
            var config = (Config)TelegramUtils.RunSynchronously(client.Methods.HelpGetConfigAsync(new HelpGetConfigArgs
                {
                }));
            _config = config;
        }
 

        private void GetDialogs(TelegramClient client)
        {
            DebugPrint("Fetching conversations");

            _dialogs = new CachedDialogs();

            if (!_dialogs.DatabasesExist())
            {
                FetchNewDialogs(client);
                return;
            }

            DebugPrint("Obtained conversations.");
        }

        private void FetchNewDialogs(TelegramClient client)
        {
            DebugPrint("Databases dont exist! creating new ones from data from server");
            var iDialogs =
                TelegramUtils.RunSynchronously(client.Methods.MessagesGetDialogsAsync(new MessagesGetDialogsArgs
                {
                    Limit = 100,
                    OffsetPeer = new InputPeerEmpty(),
                }));
            var messagesDialogs = iDialogs as MessagesDialogs;

            var messagesDialogsSlice = iDialogs as MessagesDialogsSlice;

            if (messagesDialogs != null)
            {
                _dialogs.AddChats(messagesDialogs.Chats);
                _dialogs.AddUsers(messagesDialogs.Users);
                if (LoadConversations)
                {
                    LoadLast10Conversations(messagesDialogs);
                }
            }
            else if (messagesDialogsSlice != null)
            {
                //first add whatever we have got until now
                _dialogs.AddChats(messagesDialogsSlice.Chats);
                _dialogs.AddUsers(messagesDialogsSlice.Users);
                if (LoadConversations)
                {
                    LoadLast10Conversations(messagesDialogsSlice);
                }
                var numDialogs = 0;
                do
                {
                    numDialogs = messagesDialogsSlice.Dialogs.Count;

                    DebugPrint("%%%%%%% Number of Dialogs " + numDialogs);

                    var lastDialog = messagesDialogsSlice.Dialogs.LastOrDefault() as Dialog;

                    if (lastDialog != null)
                    {
                        var lastPeer = GetInputPeerFromIPeer(lastDialog.Peer);
                        var offsetId = Math.Max(lastDialog.ReadInboxMaxId, lastDialog.TopMessage);
                        var offsetDate = FindDateForMessageId(offsetId, messagesDialogsSlice);
                        var nextDialogs = TelegramUtils.RunSynchronously(client.Methods.MessagesGetDialogsAsync(new MessagesGetDialogsArgs
                        {
                            Limit = 100,
                            OffsetPeer = lastPeer,
                            OffsetId = offsetId,
                            OffsetDate = offsetDate
                        }));

                        messagesDialogsSlice = nextDialogs as MessagesDialogsSlice;
                        if (messagesDialogsSlice == null)
                        {
                            DebugPrint("%%%%%%% Next messages dialogs null ");
                            break;
                        }

                        _dialogs.AddUsers(messagesDialogsSlice.Users);
                        _dialogs.AddChats(messagesDialogsSlice.Chats);
                    }

                    DebugPrint("%%%%%%% Number of Dialogs At end " + numDialogs);

                } while (numDialogs >= 100);
            }
        }

        private void LoadLast10Conversations(IMessagesDialogs iMessagesDialogs)
        {
            var messagesDialogs = iMessagesDialogs as MessagesDialogs;
            var messagesDialogsSlice = iMessagesDialogs as MessagesDialogsSlice;

            if (messagesDialogs != null)
            {
                if (messagesDialogs.Dialogs.Count >= 10)
                {
                    LoadMessages(messagesDialogs.Dialogs, messagesDialogs.Messages);
                }
            }
            if (messagesDialogsSlice != null)
            {
                if (messagesDialogsSlice.Dialogs.Count >= 10)
                {
                    LoadMessages(messagesDialogsSlice.Dialogs,messagesDialogsSlice.Messages);
                }
            }

        }

        private void LoadMessages(List<IDialog> dialogs, List<IMessage> messages)
        {
            int i = 0;

            Action<IMessage> processMessage = iMessage =>
            {
                if (iMessage != null)
                {
                    var message = iMessage as Message;
                    var messageService = iMessage as MessageService;
                    if (message != null)
                    {
                        var bubbles = ProcessFullMessage(message, false);
                        foreach (var bubble in bubbles)
                        {
                            TelegramEventBubble(bubble);
                        }
                    }
                    if (messageService != null)
                    {
						var partyInformationBubbles = MakePartyInformationBubble(messageService, false, null);
                        foreach (var partyInformationBubble in partyInformationBubbles)
                        {
                            TelegramEventBubble(partyInformationBubble);
                        }
                    }
                }
            };

            foreach (var idialog in dialogs)
            {
                var dialog = idialog as Dialog;
                if (dialog != null)
                {
                    var iMessage  = FindMessage(dialog.TopMessage, messages);
                    processMessage(iMessage);
                    if (dialog.UnreadCount == 0)
                    {
                        SetRead(iMessage);
                    }
                    if (dialog.Peer is PeerChannel)
                    { 
                        SaveChannelState(uint.Parse(TelegramUtils.GetPeerId(dialog.Peer)), dialog.Pts);
                    }
                    if (i >= 10)
                        break;
                    i++;
                }
            }
        }

        private void SetRead(IMessage iMessage)
        {
            var message = iMessage as Message;
            var messageService = iMessage as MessageService;


            if (message != null)
            {
                var direction = message.FromId == _settings.AccountId
                    ? Bubble.BubbleDirection.Outgoing
                    : Bubble.BubbleDirection.Incoming;

                var peerUser = message.ToId as PeerUser;
                var peerChat = message.ToId as PeerChat;
                var peerChannel = message.ToId as PeerChannel;

                if (peerUser != null)
                {
                    var address = direction == Bubble.BubbleDirection.Incoming
                        ? message.FromId
                        : peerUser.UserId;
                    BubbleGroupManager.SetUnread(this, false, address.ToString(CultureInfo.InvariantCulture));
                    NotificationManager.Remove(this, address.ToString(CultureInfo.InvariantCulture));
                }
                else if (peerChat != null)
                {
                    BubbleGroupManager.SetUnread(this, false, peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                    NotificationManager.Remove(this, peerChat.ChatId.ToString(CultureInfo.InvariantCulture));
                }
                else if (peerChannel != null)
                { 
                    BubbleGroupManager.SetUnread(this, false, peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture));
                    NotificationManager.Remove(this, peerChannel.ChannelId.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (messageService != null)
            {
                var address = TelegramUtils.GetPeerId(messageService.ToId);
                BubbleGroupManager.SetUnread(this, false, address);
                NotificationManager.Remove(this, address);
            }

        }

        private IMessage FindMessage(uint topMessage, List<IMessage> messages)
        {
            foreach (var iMessage in messages)
            {
                var messageId = TelegramUtils.GetMessageId(iMessage);
                if (messageId == topMessage)
                {
                    return iMessage;
                }
            }
            return null;
        }

        private IInputPeer GetInputPeerFromIPeer(IPeer peer)
        {
            IInputPeer retInputUser = null;
            var peerUser = peer as PeerUser;
            var peerChat = peer as PeerChat;
            var peerChannel = peer as PeerChannel;

            if (peerUser != null)
            {
                var inputPeerUser = new InputPeerUser
                {
                    UserId = peerUser.UserId
                };
                retInputUser = inputPeerUser;
            }
            else if (peerChat != null)
            {
                var inputPeerChat = new InputPeerChat
                {
                    ChatId = peerChat.ChatId
                };
                retInputUser = inputPeerChat;
            }
            else if(peerChannel!= null)
            {
                var inputPeerChannel = new InputPeerChannel
                {
                    ChannelId = peerChannel.ChannelId,
                    AccessHash = TelegramUtils.GetChannelAccessHash(_dialogs.GetChat(peerChannel.ChannelId))
                };
                retInputUser = inputPeerChannel;
            }
            return retInputUser;

        }

        private uint FindDateForMessageId(uint offsetId, MessagesDialogsSlice messagesDialogsSlice)
        {
            uint date = 0;
            foreach (var iMessage in messagesDialogsSlice.Messages)
            {
                var message = iMessage as Message;
                if (message != null)
                {
                    if (message.Id == offsetId)
                    {
                        date = message.Date;
                    }
                }
                var messageService = iMessage as MessageService;
                if (messageService != null)
                {
                    if (messageService.Id == offsetId)
                    {
                        date = messageService.Date;
                    }
                }

            }
            return date;
        }

        private byte[] FetchDocumentBytes(SharpTelegram.Schema.Document document, uint offset, uint limit)
        {
            if (document.DcId == _settings.NearestDcId)
            {
                using (var clientDisposable = new FullClientDisposable(this))
                {
                    return FetchDocumentBytes(clientDisposable.Client, document, offset, limit);
                }
            }
            else
            {
                try
                {
                    if (cachedClient == null)
                    {
                        cachedClient = GetClient((int) document.DcId);
                    }
                    return FetchDocumentBytes(cachedClient, document, offset, limit);
                }
                catch (Exception ex)
                {
                    DebugPrint("Failed to obtain client from DC manager: " + ex);
                    return null;
                }
            }
        }

        private byte[] FetchDocumentBytes(TelegramClient client, SharpTelegram.Schema.Document document, uint offset, uint limit)
        {
            var response = (UploadFile)TelegramUtils.RunSynchronously(client.Methods.UploadGetFileAsync(
                new UploadGetFileArgs
                {
                    Location = new InputDocumentFileLocation
                    {
                        AccessHash = document.AccessHash,
                        Id = document.Id
                    },
                    Offset = offset,
                    Limit = limit,
                }));
            return response.Bytes;
        }
    }
}

