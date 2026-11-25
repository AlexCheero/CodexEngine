using System;
using System.Collections.Generic;
using Brutal.JoltApi;

namespace CodexEngine.Runtime;

/// <summary>
/// Two bodies that started touching during a step: both colliders, the first contact point (on the first body) and the
/// normal from the first toward the second, in world space, and how far they overlap.
/// </summary>
public readonly record struct ContactStart(Collider First, Collider Second, System.Numerics.Vector3 Point, System.Numerics.Vector3 Normal, float Depth);

public partial class CollisionWorld
{
    private ContactEventListener contactEvents;
    private ContactEvent[] contactBuffer;

    /// <summary>
    /// Records every new contact from now on (not the per-step persisted ones), buffering up to
    /// <paramref name="capacity"/> between drains; what does not fit is dropped.
    /// </summary>
    public void ListenForContacts(int capacity = 4096)
    {
        if (contactEvents != null) return;
        contactEvents = new ContactEventListener((uint)capacity, ContactEventMask.Added);
        contactBuffer = new ContactEvent[Math.Min(capacity, 1024)];
        system.SetContactListener(contactEvents.Handle);
    }

    /// <summary>
    /// Hands the contacts that started since the last drain (or clear) to <paramref name="into"/>, oldest first. A contact
    /// whose body has gone since is left out. Drain right after the step: body ids are reused once a body is removed.
    /// </summary>
    public void DrainContacts(List<ContactStart> into)
    {
        if (contactEvents == null) return;
        uint remaining;
        do
        {
            int count = (int)contactEvents.Drain(contactBuffer, out remaining);
            for (int i = 0; i < count; i++)
            {
                ref readonly var e = ref contactBuffer[i];
                if (!nodes.TryGetValue(e.Body1, out var first) || !nodes.TryGetValue(e.Body2, out var second)) continue;
                into.Add(new ContactStart(first, second, new(e.Point.X, e.Point.Y, e.Point.Z), new(e.Normal.X, e.Normal.Y, e.Normal.Z), e.PenetrationDepth));
            }
            if (count == 0) break;
        } while (remaining > 0);
    }

    /// <summary>Forgets the contacts recorded so far (steps taken outside the game's own loop, say).</summary>
    public void ClearContacts() => contactEvents?.Clear();

    private void ReleaseContacts()
    {
        if (contactEvents == null) return;
        contactEvents.Dispose();
        contactEvents = null;
    }
}
