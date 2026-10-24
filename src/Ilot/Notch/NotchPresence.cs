namespace Ilot.Notch;

/// <summary>What a module has for the closed notch (see <see cref="NotchContext.SetPresence"/>).</summary>
public enum NotchPresence
{
    /// <summary>
    /// Shown elsewhere (the player on the taskbar): the closed notch rests at once, in its neutral state (the mascot in
    /// its nest, or its handle), even when it would keep its pill otherwise (« Se cacher au repos » off).
    /// </summary>
    Elsewhere = -1,

    /// <summary>Nothing to show: the notch can be just its handle, at once.</summary>
    Nothing,

    /// <summary>Something shown, nothing going on (a track paused): the handle after a while (see the settings).</summary>
    Idle,

    /// <summary>Something going on (music playing): the pill stays.</summary>
    Active,
}
