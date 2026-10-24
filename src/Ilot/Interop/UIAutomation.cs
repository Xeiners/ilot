using System.Runtime.InteropServices;

namespace Ilot.Interop;

// UI Automation, just what reading the taskbar takes: its elements (the taskbar of Windows 11 is XAML, its buttons are no
// windows), their kind and where they are. Each interface lists its methods in their vtable order; those not used are
// placeholders that only keep the slots.

/// <summary>The UI Automation client (CUIAutomation).</summary>
[ComImport]
[Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
internal class CUIAutomation
{
}

[ComImport]
[Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void CompareElements();

    void CompareRuntimeIds();

    void GetRootElement();

    IUIAutomationElement ElementFromHandle(nint hwnd);

    void ElementFromPoint();

    void GetFocusedElement();

    void GetRootElementBuildCache();

    void ElementFromHandleBuildCache();

    void ElementFromPointBuildCache();

    void GetFocusedElementBuildCache();

    void CreateTreeWalker();

    void ControlViewWalker();

    void ContentViewWalker();

    void RawViewWalker();

    void RawViewCondition();

    void ControlViewCondition();

    void ContentViewCondition();

    void CreateCacheRequest();

    IUIAutomationCondition CreateTrueCondition();
}

[ComImport]
[Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

[ComImport]
[Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElementArray
{
    int GetLength();

    IUIAutomationElement GetElement(int index);
}

[ComImport]
[Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void SetFocus();

    void GetRuntimeId();

    void FindFirst();

    IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);

    void FindFirstBuildCache();

    void FindAllBuildCache();

    void BuildUpdatedCache();

    void GetCurrentPropertyValue();

    void GetCurrentPropertyValueEx();

    void GetCachedPropertyValue();

    void GetCachedPropertyValueEx();

    void GetCurrentPatternAs();

    void GetCachedPatternAs();

    void GetCurrentPattern();

    void GetCachedPattern();

    void GetCachedParent();

    void GetCachedChildren();

    void CurrentProcessId();

    int CurrentControlType();

    void CurrentLocalizedControlType();

    void CurrentName();

    void CurrentAcceleratorKey();

    void CurrentAccessKey();

    void CurrentHasKeyboardFocus();

    void CurrentIsKeyboardFocusable();

    void CurrentIsEnabled();

    void CurrentAutomationId();

    void CurrentClassName();

    void CurrentHelpText();

    void CurrentCulture();

    void CurrentIsControlElement();

    void CurrentIsContentElement();

    void CurrentIsPassword();

    void CurrentNativeWindowHandle();

    void CurrentItemType();

    int CurrentIsOffscreen();

    void CurrentOrientation();

    void CurrentFrameworkId();

    void CurrentIsRequiredForForm();

    void CurrentItemStatus();

    NativeMethods.Rect CurrentBoundingRectangle();
}
