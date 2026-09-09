namespace DashDeck.Host.Sensors;

/// <summary>
/// The tablet's own sensors plus the phone's GPS, behind one <see cref="IDeviceSensors"/>.
/// </summary>
/// <remarks>
/// Lets <see cref="SensorService"/> stay unchanged (ADR-0027): it still asks one device for a
/// reading, and this decides who owns the channel. Location channels go to the phone; the
/// <em>heading</em> channel goes to the phone's GPS course while moving and falls through to the
/// tablet magnetometer otherwise; everything else — pitch, roll, G — is the tablet's. Levelling
/// is the tablet's alone, since the phone has no mount to level against.
/// </remarks>
public sealed class CompositeDeviceSensors : IDeviceSensors
{
    private readonly IDeviceSensors _tablet;
    private readonly PhoneLocationSensors _phone;

    public CompositeDeviceSensors(IDeviceSensors tablet, PhoneLocationSensors phone)
    {
        _tablet = tablet;
        _phone = phone;
    }

    /// <inheritdoc />
    public bool Has(SensorDefinition definition) =>
        definition.Source is SensorSource.Gps ? _phone.Has(definition) : _tablet.Has(definition);

    /// <inheritdoc />
    public SensorReading Read(SensorDefinition definition, MountReference reference)
    {
        if (definition.Source is SensorSource.Gps)
        {
            return _phone.Read(definition, reference);
        }

        // Heading prefers the GPS course while moving; the magnetometer answers when it declines.
        if (definition.Channel is SensorChannel.Heading && _phone.TryReadHeading(out var heading))
        {
            return heading;
        }

        return _tablet.Read(definition, reference);
    }

    /// <inheritdoc />
    public MountReference? CaptureReference(DateTimeOffset nowUtc) => _tablet.CaptureReference(nowUtc);

    /// <inheritdoc />
    public void Dispose()
    {
        _tablet.Dispose();
        _phone.Dispose();
    }
}
