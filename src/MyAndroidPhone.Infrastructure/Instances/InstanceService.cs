using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Instances;
using MyAndroidPhone.Core.Results;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Infrastructure.Instances;

public sealed class InstanceService : JsonStateService<InstanceStore>, IInstanceService
{
    public const int MaxNameLength = 40;

    public InstanceService(AppPaths paths, ILogger<InstanceService> logger)
        : base(paths.InstancesFile, logger)
    {
    }

    public IReadOnlyList<AndroidInstance> Instances =>
        Current.Instances.OrderByDescending(i => i.LastOpened ?? i.CreatedAt).ToList();

    public AndroidInstance? Find(Guid id) => Current.Instances.FirstOrDefault(i => i.Id == id);

    public async Task<OperationResult<AndroidInstance>> CreateAsync(AndroidInstance instance, CancellationToken cancellationToken = default)
    {
        var name = (instance.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return OperationResult<AndroidInstance>.Failure(ErrorKind.InvalidInput, $"Give the instance a name of 1 to {MaxNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(instance.DeviceSerial))
        {
            return OperationResult<AndroidInstance>.Failure(ErrorKind.InvalidInput, "Choose a connected device first.");
        }

        OperationError? error = null;
        AndroidInstance? created = null;
        await UpdateAsync(store =>
        {
            var sameDevice = store.Instances.FirstOrDefault(i => string.Equals(i.DeviceSerial, instance.DeviceSerial, StringComparison.Ordinal));
            if (sameDevice is not null)
            {
                error = new OperationError(ErrorKind.InvalidInput, $"This device already has an instance called \"{sameDevice.Name}\".");
                return;
            }

            if (store.Instances.Any(i => string.Equals(i.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                error = new OperationError(ErrorKind.InvalidInput, $"An instance named \"{name}\" already exists. Choose another name.");
                return;
            }

            created = new AndroidInstance
            {
                Id = Guid.NewGuid(),
                Name = name,
                IconGlyph = instance.IconGlyph,
                AccentColor = instance.AccentColor,
                Theme = instance.Theme,
                DeviceSerial = instance.DeviceSerial,
                ConnectionType = instance.ConnectionType,
                Endpoint = instance.Endpoint,
                DeviceModel = instance.DeviceModel,
                CreatedAt = DateTimeOffset.Now,
                LastOpened = null
            };
            store.Instances.Add(created);
        }, cancellationToken).ConfigureAwait(false);

        return error is not null ? OperationResult<AndroidInstance>.Failure(error) : OperationResult<AndroidInstance>.Success(created!);
    }

    public async Task<OperationResult<AndroidInstance>> UpdateAsync(Guid id, Action<AndroidInstance> change, CancellationToken cancellationToken = default)
    {
        OperationError? error = null;
        AndroidInstance? updated = null;
        await UpdateAsync(store =>
        {
            var index = store.Instances.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                error = new OperationError(ErrorKind.NotFound, "That instance no longer exists.");
                return;
            }

            // Validate a copy so a rejected change never reaches the file.
            var candidate = store.Instances[index].Clone();
            change(candidate);
            candidate.Name = (candidate.Name ?? string.Empty).Trim();
            if (candidate.Name.Length == 0 || candidate.Name.Length > MaxNameLength || candidate.Name.Any(char.IsControl))
            {
                error = new OperationError(ErrorKind.InvalidInput, $"Give the instance a name of 1 to {MaxNameLength} characters.");
                return;
            }

            if (store.Instances.Any(i => i.Id != id && string.Equals(i.Name, candidate.Name, StringComparison.CurrentCultureIgnoreCase)))
            {
                error = new OperationError(ErrorKind.InvalidInput, $"An instance named \"{candidate.Name}\" already exists.");
                return;
            }

            store.Instances[index] = candidate;
            updated = candidate;
        }, cancellationToken).ConfigureAwait(false);

        return error is not null ? OperationResult<AndroidInstance>.Failure(error) : OperationResult<AndroidInstance>.Success(updated!);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(store => store.Instances.RemoveAll(i => i.Id == id), cancellationToken);

    public Task MarkOpenedAsync(Guid id, CancellationToken cancellationToken = default) =>
        UpdateAsync(store =>
        {
            var target = store.Instances.FirstOrDefault(i => i.Id == id);
            if (target is not null)
            {
                target.LastOpened = DateTimeOffset.Now;
            }
        }, cancellationToken);
}
