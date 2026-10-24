namespace MyNotch.Notch;

/// <summary>The keyboard, taken by a module (see <see cref="NotchContext.TakeKeyboard"/>).</summary>
internal interface IKeyboardLease : IDisposable
{
    /// <summary>Ends the lease without handing the keyboard back (see <see cref="NotchContext.HandOffKeyboard"/>).</summary>
    void HandOff();
}
