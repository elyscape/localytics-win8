# A Windows 8 port of the Localytics instrumentation classes

[Localytics](http://www.localytics.com/) does not provide an instrumentation library specifically for Windows 8, instead [directing developers to use the HTML5 library](http://www.localytics.com/docs/windows-8-integration/).  Unfortunately, the HTML5 library is not usable by non-HTML5 applications.  This project is a port of [the Windows Phone 7 instrumentation library](http://www.localytics.com/docs/windows-phone-7-integration/) that works in Windows 8 .NET Metro applications.  It _may_ also work in .NET Desktop applications compiled with WinRT support.

## How to instrument your Windows 8 app
### Setup
Instrumenting a Windows 8 app is almost identical to [instrumenting a WP7 app](http://www.localytics.com/docs/windows-phone-7-integration/), but the names of some methods have changed.  Put these lines:
````csharp
appSession = new LocalyticsSession("app key");
appSession.open();
appSession.upload();
````
at the beginning of OnLaunched().  Yes, before you check to see if you're were already running.  So it should look something like this:
````csharp
protected override async void OnLaunched(LaunchActivatedEventArgs args)
{
  appSession = new LocalyticsSession("app key");
  appSession.open();
  appSession.upload();
  
  // Do not repeat app initialization when already running, just ensure that
  // the window is active
  if (args.PreviousExecutionState == ApplicationExecutionState.Running)
  {
    Window.Current.Activate();
    return;
  }
...
````
You'll also want to put that at the beginning of OnActivated(), if your app offers any other methods of activation (e.g. search contract, file picker).

Make sure to add a handler to the Suspending event (this has already been done on layout-aware pages as OnSuspending).  At the beginning of OnSuspending(), put this line:
````csharp
appSession.close();
````
### Tagging events (optional)
To tag an event, use the following line of code:
````csharp
((App)Application.Current).appSession.tagEvent("Event Name");
````
You can attach attributes to an event by passing a Dictionary<string, string> as a second argument to tagEvent().  For example, to collect information about unhandled exceptions, you could use the following code:
````csharp
private void OnUnhandledException(object sender, UnhandledExceptionEventArgs unhandledExceptionEventArgs)
{
  if (appSession == null)
  {
    return;
  }
  var dict = new Dictionary<string, string>();
  dict.Add("Message",unhandledExceptionEventArgs.Message);
  dict.Add("Stack trace", unhandledExceptionEventArgs.Exception.StackTrace);
  ((App)Application.Current).appSession.tagEvent("Unhandled exception", dict);
  appSession.close();
}
````
## Notes
- Screen flows are not currently supported.  The WP7 library lacks screen flow support, and this was a straight port.  Feel free to port it over from the Android library.  I may do this myself, but no promises.
- There's no way that I can find for a Metro app to get the OS version or device information.  I ended up reporting the OS version as whatever the compiled architecture target was (i.e. x86, x64, or ARM).  Depending on how your code is compiled, this may report "Neutral".  Additionally, it does not report the device manufacturer and claims a device model of "Computer".
- The device UUID is a hash based on the [HardwareIdentification.GetPackageSpecificToken()](http://msdn.microsoft.com/EN-US/library/windows/apps/windows.system.profile.hardwareidentification.getpackagespecifictoken.aspx) method and therefore __may change if the hardware changes__.  [More information is available from Microsoft](http://msdn.microsoft.com/en-us/library/windows/apps/jj553431#structure_of_an_ashwid).
- The library version currently reports "windowsphone_2.2".  This isn't reported to the user, instead being used by Localytics for some internal purposes, but I figured it was worth noting.  I haven't had a chance to test out whether "windows8_2.0" works.
- The code is not great in some places.  I'm comfortable blaming most of that on Localytics.  I may do some cleanup later.