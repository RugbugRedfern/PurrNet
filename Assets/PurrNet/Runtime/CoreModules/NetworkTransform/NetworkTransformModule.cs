using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Transports;
using Unity.Profiling;

namespace PurrNet.Modules
{
    public struct NetworkTransformDelta : IPackedAuto
    {
        public SceneID scene;
        public readonly ByteData packet;

        public NetworkTransformDelta(SceneID context, BitPacker packer)
        {
            scene = context;
            packet = packer.ToByteData();
        }
    }

    public class NetworkTransformModule : INetworkModule, IPromoteToServerModule
    {
        static readonly ProfilerMarker _postFixedUpdateMarker = new ProfilerMarker("NetworkTransform.PostFixedUpdate");
        static readonly ProfilerMarker _gatherStateMarker = new ProfilerMarker("NetworkTransform.GatherState");
        static readonly ProfilerMarker _prepareDeltaMarker = new ProfilerMarker("NetworkTransform.PrepareDeltaState");

        private readonly List<NetworkTransform> _networkTransforms = new();

        // PERF: transforms that changed this tick, built once per tick and shared by every
        // PrepareDeltaState call, instead of re-scanning every transform once per player.
        // Kept in the same (id-sorted) order as _networkTransforms so the NetworkID delta packing stays small.
        private readonly List<NetworkTransform> _changed = new();

        private readonly ScenePlayersModule _scenePlayers;
        private readonly PlayersBroadcaster _broadcaster;
        private readonly NetworkManager _manager;
        private readonly SceneID _scene;
        private readonly HierarchyFactory _factory;
        private bool _asServer;

        public NetworkTransformModule(NetworkManager manager, PlayersBroadcaster broadcaster,
            ScenePlayersModule scenePlayers, SceneID scene, HierarchyFactory factory)
        {
            _manager = manager;
            _scenePlayers = scenePlayers;
            _broadcaster = broadcaster;
            _scene = scene;
            _factory = factory;
        }

        public void PromoteToServerModule()
        {
            _asServer = true;
        }

        public void PostPromoteToServerModule()
        {
        }

        public void Enable(bool asServer)
        {
            _asServer = asServer;
            _broadcaster.Subscribe<NetworkTransformDelta>(OnNetworkTransformDelta);
        }

        public void Disable(bool asServer)
        {
            _broadcaster.Unsubscribe<NetworkTransformDelta>(OnNetworkTransformDelta);
        }

        private void OnNetworkTransformDelta(PlayerID player, NetworkTransformDelta data, bool asServer)
        {
            if (data.scene != _scene)
                return;

            using var packet = BitPackerPool.Get(data.packet);

            packet.ResetPositionAndMode(true);

            int ntCount = default;
            NetworkID lastNid = default;
            PackedInt lastLen = default;

            Packer<int>.Read(packet, ref ntCount);

            for (var i = 0; i < ntCount; i++)
            {
                PackedInt length = default;
                DeltaPacker<PackedInt>.Read(packet, lastLen, ref length);
                lastLen = length;
                DeltaPacker<NetworkID>.Read(packet, lastNid, ref lastNid);

                if (_factory.TryGetIdentity(_scene, lastNid, out var identity) && identity is NetworkTransform nt &&
                    (!asServer || nt.IsControlling(player, false)))
                {
                    nt.DeltaRead(packet);
                }
                else packet.SkipBits(length);
            }
        }

        private PlayerID GetLocalPlayer()
        {
            if (_manager.TryGetModule<PlayersManager>(false, out var _players))
                return _players.localPlayerId.GetValueOrDefault();
            return PlayerID.Server;
        }

        /// <summary>
        /// Writes the deltas from <see cref="_changed"/> that should go to <paramref name="player"/>.
        /// PERF: only walks the changed list (usually small) rather than every transform in the scene,
        /// and doesn't call HasChanges() a second time.
        /// </summary>
        private bool PrepareDeltaState(BitPacker packer, PlayerID player)
        {
            using var _ = _prepareDeltaMarker.Auto();

            bool toServer = player == PlayerID.Server;

            NetworkID lastNid = default;
            PackedInt lastLen = default;
            int writtenCount = 0;

            var countPos = packer.positionInBits;
            Packer<int>.Write(packer, 0);

            using var tmp = BitPackerPool.Get();

            int changedCount = _changed.Count;
            for (var i = 0; i < changedCount; i++)
            {
                var nt = _changed[i];

                // Same filter the original GatherCandidates used for non-server targets:
                // don't send a player their own object, and only send what they can see.
                if (!toServer && (nt.IsControlling(player, false) || !nt.IsObserver(player)))
                    continue;

                nt.DeltaWrite(tmp);

                PackedInt length = tmp.positionInBits;

                tmp.ResetPositionAndMode(true);

                DeltaPacker<PackedInt>.Write(packer, lastLen, length);
                lastLen = length;
                DeltaPacker<NetworkID>.Write(packer, lastNid, nt.id!.Value);
                packer.WriteBits(tmp, length);

                tmp.ResetPositionAndMode(false);

                lastNid = nt.id.Value;
                writtenCount += 1;
            }

            var lastPos = packer.positionInBits;
            packer.SetBitPosition(countPos);
            Packer<int>.Write(packer, writtenCount);
            packer.SetBitPosition(lastPos);

            return writtenCount > 0;
        }

        public void Register(NetworkTransform networkTransform)
        {
            if (!networkTransform.id.HasValue)
                return;
            AddTrs(networkTransform);
        }

        private void AddTrs(NetworkTransform networkTransform)
        {
            if (_networkTransforms.Contains(networkTransform))
                return;

            for (int i = 0; i < _networkTransforms.Count; i++)
            {
                var networkID = _networkTransforms[i].id;
                if (networkID != null && networkTransform.id != null &&
                    networkID.Value > networkTransform.id.Value)
                {
                    _networkTransforms.Insert(i, networkTransform);
                    return;
                }
            }

            _networkTransforms.Add(networkTransform);
        }

        public void Unregister(NetworkTransform networkTransform)
        {
            _networkTransforms.Remove(networkTransform);
        }

        public void PostFixedUpdate()
        {
            using var _ = _postFixedUpdateMarker.Auto();

            // PERF: when hosting, the client-side copy of this module would only send the host's own
            // objects to the server running in this same process (a loopback to ourselves). The
            // server-side copy already gathers and sends everything remote clients need, so skip it.
            if (!_asServer && _manager.isServer)
                return;

            var localPlayer = GetLocalPlayer();
            int ntCount = _networkTransforms.Count;

            // PERF: one pass over every transform per tick. Gather state and collect the ones that
            // changed; everything after this only touches the changed list.
            _changed.Clear();

            _gatherStateMarker.Begin();
            for (var i = 0; i < ntCount; i++)
            {
                var nt = _networkTransforms[i];

                if (!nt.IsControlling(localPlayer, _asServer))
                    continue;

                nt.GatherState();

                if (nt.IsSpawned(_asServer) && nt.id.HasValue && nt.HasChanges())
                    _changed.Add(nt);
            }
            _gatherStateMarker.End();

            // Nothing moved this tick: nothing to send and nothing to save.
            if (_changed.Count == 0)
                return;

            if (!_asServer)
            {
                using var packer = BitPackerPool.Get();

                if (PrepareDeltaState(packer, PlayerID.Server) && packer.positionInBits > 0)
                    _broadcaster.SendToServer(new NetworkTransformDelta(_scene, packer));
            }
            else if (_scenePlayers.TryGetPlayersInScene(_scene, out var players))
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    if (player == localPlayer)
                        continue;

                    using var packer = BitPackerPool.Get();

                    if (PrepareDeltaState(packer, player) && packer.positionInBits > 0)
                        _broadcaster.Send(player, new NetworkTransformDelta(_scene, packer));
                }
            }

            // Unchanged transforms already have _lastSentDelta == _currentData, so saving only the
            // changed ones is equivalent to the original loop over every controlled transform.
            for (var i = 0; i < _changed.Count; i++)
                _changed[i].DeltaSave();
        }
    }
}
