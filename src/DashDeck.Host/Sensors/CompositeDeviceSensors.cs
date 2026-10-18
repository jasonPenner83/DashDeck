namespace DashDeck.Host.Sensors;

/// <summary>
/// The tablet's own sensors plus the phone's GPS, behind one <see cref="IDeviceSensors"/>.
/// </summary>
/// <remarks>
/// Lets <see cref="SensorService"/> stay unchanged (ADR-0027): it still asks one device for a
/// reading, and this decides who owns the channel. Location channels go to the phone; the
/// <em>heading</em> channel goes to the phone's GPS course while moving and falls through to the
/// tablet magnetometer otherwise; the navigation channels (ADR-0066) go to the dash's own
/// arithmetic; everything else — pitch, roll, G — is the tablet's. Levelling is the tablet's
/// alone, since the phone has no mount to level against.
/// <para>
/// Three providers rather than two, and the shape held: adding navigation meant one more arm on
/// two switch statements and nothing else, which is the test of whether this seam was worth
/// having.
/// </para>
/// </remarks>
public sealed class CompositeDeviceSensors : IDeviceSensors
{
    private readonly IDeviceSensors _tablet;
    private readonly PhoneLocationSensors _phone;
    private readonly NavigationSensors? _navigation;

    /// <param name="navigation">
    /// The navigation readouts, or null for a dash built without a map — in which case the
    /// navigation rows in the catalog read as having no sensor behind them, which is the truth.
    /// </param>
    public CompositeDeviceSensors(
        IDeviceSensors tablet,
        PhoneLocationSensors phone,
        NavigationSensors? navigation = null)
    {
        _tablet = tablet;
        _phone = phone;
        _navigation = navigation;
    }

    /// <inheritdoc />
    public bool Has(SensorDefinition definition) => definition.Source switch
    {
        SensorSource.Gps => _phone.Has(definition),
        SensorSource.Navigation => _navigation?.Has(definition) ?? false,
        _ => _tablet.Has(definition),
    };

    /// <inheritdoc />
    public SensorReading Read(SensorDefinition definition, MountReference reference)
    {
        if (definition.Source is SensorSource.Gps)
        {
            return _phone.Read(definition, reference);
        }

        if (definition.Source is SensorSource.Navigation)
        {
            return _navigation?.Read(definition, reference) ?? SensorReading.None("NO NAVIGATION");
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
        _navigation?.Dispose();
    }
}
