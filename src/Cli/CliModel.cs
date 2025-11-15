// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.CommandLine;
using System.CommandLine.Invocation;
using BuildXL.Cache.ContentStore.UtilitiesCore.Internal;
using BuildXL.Utilities;

namespace FastDownload;

internal static class CliModel
{
    public static void Bind<T>(Command command, Func<CliModel<T>, T> getOptions, Func<T, Task> runAsync)
    {
        var model = new CliModel<T>(command);
        getOptions(model);

        // Disable options mode so that real target values get created in handler
        model.OptionsMode = false;

        command.SetHandler(context =>
        {
            var target = getOptions(model);
            model.Apply(target, context);
            return runAsync(target);
        });
    }
}

internal class CliModel<T>(Command command)
{
    public bool OptionsMode { get; set; } = true;

    public delegate ref TField RefFunc<TField>(T model);

    private List<(Action<T, InvocationContext, bool> Action, int Priority)> SetFields { get; } = new();

    public void Apply(T target, InvocationContext context)
    {
        // First set defaults. Then set explicit values
        foreach (var implicitPhase in new bool[] { true, false })
        {
            foreach (var item in SetFields.OrderBy(t => t.Priority))
            {
                item.Action(target, context, implicitPhase);
            }
        }
    }

    public TField Option<TField>(RefFunc<TField> getFieldRef, string name, string? description = null, bool required = false, Optional<TField> defaultValue = default, bool isHidden = false, int setPriority = 0, Action<T>? afterSet = null)
    {
        if (OptionsMode)
        {
            name = name.StartsWith("--") ? name : $"--{name}";

            var option = defaultValue.HasValue
                ? new Option<TField>(name, getDefaultValue: () => defaultValue.Value, description: description)
                : new Option<TField>(name, description: description);

            option.IsRequired = required;
            option.IsHidden = isHidden;

            // Allow argument to overridden by specifying again in command line
            option.AllowMultipleArgumentsPerToken = true;

            SetFields.Add(((model, context, implicitPhase) =>
            {
                var result = context.ParseResult.FindResultFor(option);
                if (result != null && result.IsImplicit == implicitPhase)
                {
                    getFieldRef(model) = context.ParseResult.GetValueForOption(option)!;

                    afterSet?.Invoke(model);
                }
            },
            setPriority));

            command.AddOption(option);
        }

        return default!;
    }
}