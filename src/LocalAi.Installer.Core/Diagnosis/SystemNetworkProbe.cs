using System.Net.NetworkInformation;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

/// <param name="isNetworkAvailable">Stands in for the operating system's answer in a test.</param>
public sealed class SystemNetworkProbe(Func<bool>? isNetworkAvailable = null) : INetworkProbe
{
    private readonly Func<bool> isNetworkAvailable =
        isNetworkAvailable ?? NetworkInterface.GetIsNetworkAvailable;

    public Task<NetworkSnapshot> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // No reason with the state: the page words it in whatever language it is showing.
            return Task.FromResult(new NetworkSnapshot(
                isNetworkAvailable() ? ObservationState.Available : ObservationState.Unavailable,
                null));
        }
        catch (NetworkInformationException exception)
        {
            return Task.FromResult(
                new NetworkSnapshot(ObservationState.Failed, exception.Message));
        }
    }
}
