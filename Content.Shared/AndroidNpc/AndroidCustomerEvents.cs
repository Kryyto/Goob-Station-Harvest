// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.AndroidNpc;

/// <summary>
/// Raised on an android when it has finished waiting for its bar/kitchen order.
/// Drives the "loading bar above the head" visual via the normal DoAfter progress bar.
/// If <see cref="DoAfterEvent.Cancelled"/> is false, the wait timed out and the android
/// should leave grumpy without tipping. If it is true, the android was either served
/// (cancelled deliberately by the server) or otherwise interrupted.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class AndroidCustomerWaitDoAfterEvent : SimpleDoAfterEvent
{
}
