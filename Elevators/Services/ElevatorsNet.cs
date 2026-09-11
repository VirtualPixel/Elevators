using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace Elevators.Services
{
    // The bit of the mod that talks. The host writes this level's floors and lift pads into one
    // room property, so every client (late joiners too) builds the same elevator cars in the same
    // spots. Rides go out as an RPC so every client plays the doors and the rider fades and moves
    // itself. Guests say hello once so the host knows who can take the smooth ride and who still
    // needs the tumble.
    //
    // Lives on PunManager's GameObject so the RPCs dispatch by name with no ViewID plumbing.
    public class ElevatorsNet : MonoBehaviourPunCallbacks
    {
        private const string Key = "Elevators";

        internal static ElevatorsNet? Instance { get; private set; }

        private static readonly HashSet<int> modded = new HashSet<int>();
        private static int appliedNonce;

        private void Awake()
        {
            Instance = this;
            if (PhotonNetwork.InRoom)
                Hello();
        }

        public override void OnJoinedRoom() => Hello();

        public override void OnMasterClientSwitched(Player newMasterClient) => Hello();

        public override void OnPlayerLeftRoom(Player otherPlayer)
        {
            if (otherPlayer != null)
                modded.Remove(otherPlayer.ActorNumber);
        }

        public override void OnLeftRoom()
        {
            modded.Clear();
            appliedNonce = 0;
        }

        public override void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
        {
            if (propertiesThatChanged != null && propertiesThatChanged.ContainsKey(Key))
                TryApply();
        }

        private void Hello()
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient)
                return;
            photonView.RPC(nameof(ElevatorsHelloRPC), RpcTarget.MasterClient);
        }

        [PunRPC]
        private void ElevatorsHelloRPC(PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (!PhotonNetwork.IsMasterClient || _info.Sender == null)
                return;
            modded.Add(_info.Sender.ActorNumber);
            Plugin.LogInfo($"[Net] {_info.Sender.NickName} runs Elevators, smooth rides for them");
        }

        // Solo, yourself, or a guest who said hello: the elevator ride. Anyone else gets the tumble.
        internal static bool IsModded(PlayerAvatar avatar)
        {
            if (!SemiFunc.IsMultiplayer())
                return true;
            PhotonView view = avatar.photonView;
            if (view == null || view.Owner == null)
                return false;
            return view.Owner.IsLocal || modded.Contains(view.Owner.ActorNumber);
        }

        internal static int LocalActor => SemiFunc.IsMultiplayer() && PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0;

        internal static int ActorOf(PlayerAvatar avatar)
        {
            if (!SemiFunc.IsMultiplayer())
                return 0;
            PhotonView view = avatar.photonView;
            return view != null && view.Owner != null ? view.Owner.ActorNumber : -1;
        }

        internal static void Publish(LiftPayload payload)
        {
            if (!PhotonNetwork.InRoom)
                return;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { Key, payload.Pack() } });
        }

        internal static void ResetApplied() => appliedNonce = 0;

        // Clients only. Runs on a property change and again once the level has generated, since the
        // property can land before the level exists to put the cars in.
        internal static void TryApply()
        {
            if (SemiFunc.IsMasterClientOrSingleplayer() || !PhotonNetwork.InRoom)
                return;
            Hashtable props = PhotonNetwork.CurrentRoom.CustomProperties;
            if (props == null || !props.ContainsKey(Key))
                return;
            LiftPayload? payload = LiftPayload.Parse(props[Key] as string);
            if (payload == null || payload.Nonce == appliedNonce)
                return;
            LevelGenerator lg = LevelGenerator.Instance;
            if (!SemiFunc.RunIsLevel() || lg == null || !lg.Generated || lg.Level == null || lg.Level.name != payload.Level)
                return;
            appliedNonce = payload.Nonce;
            Session.ApplyRemote(payload);
            LiftService.Instance?.ApplyRemote(payload);
            Plugin.LogInfo($"[Net] applied host's floors for {payload.Level}: {payload.Pads.Count} pads");
        }

        // Host to everyone: the car leaves dock `from` for dock `to`.
        internal static void SendRide(int from, int to)
        {
            if (SemiFunc.IsMultiplayer() && Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsRideRPC), RpcTarget.All, from, to);
            else
                Elevator.PlayRide(from, to);
        }

        [PunRPC]
        private void ElevatorsRideRPC(int from, int to, PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (SemiFunc.MasterOnlyRPC(_info))
                Elevator.PlayRide(from, to);
        }

        // Host to everyone: the trip under way now ends further along.
        internal static void SendExtend(int dock)
        {
            if (SemiFunc.IsMultiplayer() && Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsExtendRPC), RpcTarget.Others, dock);
        }

        [PunRPC]
        private void ElevatorsExtendRPC(int dock, PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (SemiFunc.MasterOnlyRPC(_info))
                Elevator.PlayExtend(dock);
        }

        // Host to everyone: the doors are to stand open this long (zero shuts them).
        internal static void SendArrive()
        {
            if (SemiFunc.IsMultiplayer() && Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsArriveRPC), RpcTarget.All);
            else
                Elevator.Arrive();
        }

        [PunRPC]
        private void ElevatorsArriveRPC(PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (SemiFunc.MasterOnlyRPC(_info))
                Elevator.Arrive();
        }

        internal static void SendDoors(float seconds)
        {
            if (SemiFunc.IsMultiplayer() && Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsDoorsRPC), RpcTarget.All, seconds);
            else
                Elevator.HoldDoors(seconds);
        }

        [PunRPC]
        private void ElevatorsDoorsRPC(float seconds, PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (SemiFunc.MasterOnlyRPC(_info))
                Elevator.HoldDoors(seconds);
        }

        // A client whose car has just been built asks the host where the real one stands, so a
        // late joiner does not see it parked on the ground while everyone else rides it upstairs.
        internal static void AskCar()
        {
            if (SemiFunc.IsMultiplayer() && !PhotonNetwork.IsMasterClient && Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsCarAskRPC), RpcTarget.MasterClient);
        }

        [PunRPC]
        private void ElevatorsCarAskRPC(PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (!PhotonNetwork.IsMasterClient || _info.Sender == null)
                return;
            photonView.RPC(nameof(ElevatorsCarRPC), _info.Sender, Elevator.CurrentDock);
        }

        [PunRPC]
        private void ElevatorsCarRPC(int dock, PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (SemiFunc.MasterOnlyRPC(_info))
                Elevator.SnapTo(dock);
        }

        // A button press, from any client to the host, who decides if the car can go.
        internal static void RequestRide(int dock)
        {
            if (SemiFunc.IsMasterClientOrSingleplayer())
                Elevator.TryDepart(dock);
            else if (Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsCallRPC), RpcTarget.MasterClient, dock);
        }

        [PunRPC]
        private void ElevatorsCallRPC(int dock, PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (PhotonNetwork.IsMasterClient)
                Elevator.TryDepart(dock);
        }

        // Host to the other clients: a button was pressed, play the click, or the refusal.
        internal static void SendPress(int buttonView, int grabberView, bool denied)
        {
            if (SemiFunc.IsMultiplayer() && Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsPressRPC), RpcTarget.Others, buttonView, grabberView, denied);
        }

        // The player holding the roof hatch tells the others where it is; settle means they let go.
        internal static void SendHatch(float open, bool settle)
        {
            if (SemiFunc.IsMultiplayer() && Instance != null)
                Instance.photonView.RPC(nameof(ElevatorsHatchRPC), RpcTarget.Others, open, settle);
        }

        [PunRPC]
        private void ElevatorsHatchRPC(float open, bool settle, PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            Elevator.RemoteHatch(open, settle);
        }

        [PunRPC]
        private void ElevatorsPressRPC(int buttonView, int grabberView, bool denied, PhotonMessageInfo _info = default(PhotonMessageInfo))
        {
            if (SemiFunc.MasterOnlyRPC(_info))
                Elevator.PlayPress(buttonView, grabberView, denied);
        }
    }
}
