using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.API.Plugins;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Managers;
using Torch.Managers.ChatManager;
using Torch.Session;
using Torch.Utils;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;

namespace NpcFactionFixer
{
    public class NpcFactionFixer : TorchPluginBase, ITorchPlugin
    {
        private TorchSessionManager _torchSessionManager;
        private static Logger Log = LogManager.GetCurrentClassLogger();
        private static NpcFactionFixer Instance { get; set; }
        private readonly HashSet<ulong> _connecting = new HashSet<ulong>();
        private IMultiplayerManagerBase _multiBase;
        private static List<long> _realPlayerIds = new List<long>();


        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;
            _torchSessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (_torchSessionManager != null)
                _torchSessionManager.SessionStateChanged += SessionChanged;
            else
                Log.Warn("No session manager loaded!");

        }

        [ReflectedGetter(Name = "m_playerFaction", Type = typeof(MyFactionCollection))]
        private static Func<MyFactionCollection, Dictionary<long, long>> _mPlayerFaction;

        private void MyEntitiesOnOnEntityAdd(MyEntity obj)
        {
            if (!(obj is MyCharacter character) || character.IsBot || string.IsNullOrEmpty(character?.DisplayName) || !character.IsPlayer) return;

            var characterIdentity = character?.GetIdentity();
            
            if (characterIdentity == null) return;

            var characterIdentityId = characterIdentity.IdentityId;
            
            var steamId = MySession.Static.Players.TryGetSteamId(characterIdentityId);

            if (steamId == 0)
            {
                Log.Debug("Character with no steamID found");
                return;
            }

            if (!_connecting.Contains(steamId)) return;

            var playerFactions = _mPlayerFaction(MySession.Static.Factions);

            if (playerFactions == null || playerFactions.Count == 0)
            {
                Log.Debug("No faction found");
                return;
            }

            Torch.InvokeAsync(() =>
            {
                Thread.Sleep(100);
                _connecting.Remove(steamId);
                if (!playerFactions.TryGetValue(characterIdentityId, out var factionId)) return;
                var faction = MySession.Static.Factions[factionId];
                if (faction == null) return;
                if (faction.Members.ContainsKey(characterIdentityId)) return;
                MyVisualScriptLogicProvider.KickPlayerFromFaction(characterIdentityId);
                KickPlayer(characterIdentityId, faction);
            });

        }

        private void MultiBaseOnPlayerLeft(IPlayer obj)
        {
            _connecting.Remove(obj.SteamId);
        }

        private void MultiBaseOnPlayerJoined(IPlayer obj)
        {
            _connecting.Add(obj.SteamId);
        }

        private void SessionChanged(ITorchSession session, TorchSessionState state)
        {
            switch (state)
            {
                case TorchSessionState.Loading:
                    break;
                case TorchSessionState.Loaded:
                    _multiBase = Instance.Torch.CurrentSession.Managers.GetManager<IMultiplayerManagerBase>();
                    if (_multiBase != null)
                    {
                        _multiBase.PlayerJoined += MultiBaseOnPlayerJoined;
                        _multiBase.PlayerLeft += MultiBaseOnPlayerLeft;
                    }
                    else
                    {
                        Log.Warn("Multibase is Null");
                    }
                    MyEntities.OnEntityAdd += MyEntitiesOnOnEntityAdd;

                    MySession.Static.Factions.OnPlayerJoined += Factions_OnPlayerJoined;
                    var fixCount = CleanupPlayerFaction() + CleanupFaction();
                    if (fixCount == 0) break;
                    Log.Warn($"Cleaned up {fixCount} invalid faction data");
                    break;
                case TorchSessionState.Unloading:
                    var fixCountUnloading = CleanupPlayerFaction() + CleanupFaction();
                    if (fixCountUnloading == 0) break;
                    Log.Warn($"Cleaned up {fixCountUnloading} invalid faction data");
                    break;
                case TorchSessionState.Unloaded:
                    break;
            }
        }

        private static void Factions_OnPlayerJoined(MyFaction faction, long player)
        {
            if (!MySession.Static.Factions.IsNpcFaction(faction.Tag) && !faction.Tag.Equals("SPID"))return;
            KickPlayer(player,faction);
        }


        private static readonly MethodInfo ChangeFactionSuccess =
            typeof(MyFactionCollection).GetMethod("FactionStateChangeSuccess",
                BindingFlags.NonPublic | BindingFlags.Static);

        private static void KickPlayer(long id, MyFaction faction)
        {
            if (faction == null)return;
            
            //Disabled cause it was keeping some players from being removed from faction
            // if (!MySession.Static.Players.TryGetPlayerId(id, out var playerId))
            // {
            //     Log.Warn($"Can't find player with id {id}");
            //     return;
            // }

            var playerIdentity = MySession.Static.Players.TryGetIdentity(id);

            //var playerIdentity = MySession.Static.Players.TryGetPlayerIdentity(playerId);
            //Fuck you keen and your dickhead devs
            if (string.IsNullOrEmpty(playerIdentity.DisplayName)) return;
            MySession.Static.Factions.KickPlayerFromFaction(id);
            faction.AutoAcceptMember = false;
            faction.AcceptHumans = false;
            var steamId = MySession.Static.Players.TryGetSteamId(id);
            if (MySession.Static.Players.IsPlayerOnline(id) && steamId > 0)

            {
                Instance.Torch.CurrentSession.Managers.GetManager<ChatManagerServer>()?.
                    SendMessageAsOther(Instance.Torch.Config.ChatName,$"You were kicked from the NPC faction: [{faction.Tag}].",Color.Red,steamId);

                NetworkManager.RaiseStaticEvent(ChangeFactionSuccess,MyFactionStateChange.FactionMemberKick,faction.FactionId,faction.FactionId,id,
                    (long) 0,new EndpointId(steamId),null);
            }
            Log.Warn($"{playerIdentity.DisplayName} was removed from {faction.Tag}");

        }

        private static int CleanupPlayerFaction()
        {
            int count = 0;
            var playerIds = new List<MyIdentity>(MySession.Static.Players.GetAllIdentities());
            Log.Warn($"Found {playerIds.Count} Identity on the server");
            
            if (playerIds.Count == 0) return 0;
            
            foreach (var player in playerIds)
            {
                if (player.DisplayName == null || MySession.Static.Players.IdentityIsNpc(player.IdentityId)) continue;
                _realPlayerIds.Add(player.IdentityId);
                IMyFaction faction = MySession.Static.Factions.TryGetPlayerFaction(player.IdentityId);
                if (faction == null) continue;
                
                if (faction.Tag.Equals("SPID"))
                {
                    KickPlayer(player.IdentityId, (MyFaction) faction);
                    count++;
                    continue;
                }
                if (faction.Members.ContainsKey(player.IdentityId)) continue;
                KickPlayer(player.IdentityId, (MyFaction) faction);
                count++;
            }
            return count;
        }

        /// <summary>
        /// Added faction check to remove real players from NPC factions
        /// </summary>
        /// <returns></returns>
        private static int CleanupFaction()
        {
            int count = 0;
            var factions = MySession.Static.Factions;

            foreach (var (factionId, faction) in factions)
            {
                if (!MySession.Static.Factions.IsNpcFaction(faction.Tag) && !faction.Tag.Equals("SPID"))continue;
                foreach (var realPlayerId in _realPlayerIds)
                {
                    if (!faction.Members.ContainsKey(realPlayerId)) continue;
                    faction.KickMember(realPlayerId);
                    count++;
                }
            }

            return count;
        }



        public override void Dispose()
        {
            if (_multiBase != null)
            {
                _multiBase.PlayerJoined -= MultiBaseOnPlayerJoined;
                MyEntities.OnEntityAdd -= MyEntitiesOnOnEntityAdd;
                _multiBase.PlayerLeft -= MultiBaseOnPlayerLeft;

            }
            _connecting.Clear();
            _multiBase = null;

            if (_torchSessionManager != null)
                _torchSessionManager.SessionStateChanged -= SessionChanged;

            _torchSessionManager = null;

        }
    }


}
