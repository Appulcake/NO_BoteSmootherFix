### BOTE Smoother Fix
Fixes an issue for current live BOTE 1.5.2 where its network smoother prefix doesn't have a null check and can sometimes throw on 
certain invalid units, breaking `SendTransformBatcher.LateUpdate()` (since this throws in the `ClientUpdate()` => `VisualUpdate()` => 
BOTE prefix chain, and thus `SendTransformBatcher.LateUpdate()` never runs the later `Client.UpdateSent()` for example, so such a 
client will no longer be able to buy/requisite/spawn/send messages etc, in addition to most of units on map being misplaced, and 
logs being spammed with performance degradation).

Standalone implementation of: https://github.com/MinecrackTyler/BoscaliOceanTrainingExercise/pull/26/changes/dcaf34379d749de97b1f8f8c603039f348c2e93b#diff-be5007287d50cd0de053dd028f16a6f5da9f5860c016317a6407717b4c3f7b19

Also has some minor fix/logging for a seemingly related ammo RPC send issue that can arise when this issue is masked (hence why 
potentially being related, like a malformed unit initialisation from a bote/other mod/vanilla unit) which'd normally throw:
```
[RpcHandler] RPC threw an Exception: System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.
Parameter name: index
  at System.Collections.Generic.List`1[T].get_Item (System.Int32 index) [0x00009] in <1071a2cb0cb3433aae80a793c277a048>:0 
  at Unit.UserCode_RpcSyncAmmoCount_-1454761002 (System.Byte stationIndex, System.Int32 ammo) [0x00000] in <6fda19c08ef5445d835f84e9933959ab>:0 
  at Unit.Skeleton_RpcSyncAmmoCount_-1454761002 (Mirage.NetworkBehaviour behaviour, Mirage.Serialization.NetworkReader reader, Mirage.INetworkPlayer senderConnection, System.Int32 replyId) [0x00018] in <6fda19c08ef5445d835f84e9933959ab>:0 
  at Mirage.RemoteCalls.RemoteCall.Invoke (Mirage.Serialization.NetworkReader reader, Mirage.INetworkPlayer senderPlayer, System.Int32 replyId) [0x00000] in <7cd9168e2e77447ca91c7a96ffce56eb>:0 
  at Mirage.RemoteCalls.RpcHandler.HandleRpc (Mirage.INetworkPlayer player, System.UInt32 netId, System.Int32 functionIndex, System.ArraySegment`1[T] payload, System.Int32 replyId) [0x0030a] in <7cd9168e2e77447ca91c7a96ffce56eb>:0
```

This is needed on clients to be able to join and actually play a match that's had such a bad interaction as otherwise all future 
(re)joining clients have this issue, usually for rest of the match.
