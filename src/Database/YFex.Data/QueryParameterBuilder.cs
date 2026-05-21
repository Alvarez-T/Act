using System.Diagnostics;

namespace Hino.Integrador.Infra.Database;

using Dapper;
using Dapper.Oracle;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

public class QueryParameterBuilder
{
    private readonly Dictionary<string, ParameterInfo> _parameters;

    public QueryParameterBuilder()
    {
        _parameters = new Dictionary<string, ParameterInfo>(StringComparer.OrdinalIgnoreCase);
    }

    // Add a single parameter with full control
    public QueryParameterBuilder Add(string name, object? value,
        OracleMappingType? dbType = null,
        ParameterDirection? direction = null,
        int? size = null,
        bool? precision = null,
        byte? scale = null)
    {
        _parameters[name] = new ParameterInfo
        {
            Name = name,
            Value = value,
            DbType = dbType,
            Direction = direction ?? ParameterDirection.Input,
            Size = size,
            Precision = precision,
            Scale = scale
        };
        return this;
    }

    // Add parameters from an anonymous object or POCO
    public QueryParameterBuilder Add(object parameters)
    {
        if (parameters == null) return this;

        // Handle anonymous objects or POCOs
        var properties = parameters.GetType().GetProperties();
        foreach (var prop in properties)
        {
            if (_parameters.ContainsKey(prop.Name))
            {
                continue;
            }

            var value = prop.GetValue(parameters);
            Add(prop.Name, value);
        }

        return this;
    }

    // Add parameters from a dictionary
    public QueryParameterBuilder Add(IDictionary<string, object> parameters)
    {
        if (parameters == null) return this;

        foreach (var kvp in parameters)
        {
            Add(kvp.Key, kvp.Value);
        }

        return this;
    }

    // Add output parameter
    public QueryParameterBuilder AddOutput(string name, OracleMappingType dbType, int? size = null)
    {
        return Add(name, null, dbType, ParameterDirection.Output, size);
    }

    // Add input/output parameter
    public QueryParameterBuilder AddInputOutput(string name, object value, OracleMappingType dbType, int? size = null)
    {
        return Add(name, value, dbType, ParameterDirection.InputOutput, size);
    }

    // Add return value parameter
    public QueryParameterBuilder AddReturnValue(string name = "ReturnValue")
    {
        return Add(name, null, OracleMappingType.Int32, ParameterDirection.ReturnValue);
    }

    // Remove a parameter
    public QueryParameterBuilder Remove(string name)
    {
        _parameters.Remove(name);
        return this;
    }

    // Clear all parameters
    public QueryParameterBuilder Clear()
    {
        _parameters.Clear();
        return this;
    }

    // Check if parameter exists
    public bool Contains(string name)
    {
        return _parameters.ContainsKey(name);
    }

    // Get parameter value
    public T? GetValue<T>(string name)
    {
        if (_parameters.TryGetValue(name, out var param))
        {
            return (T?)param.Value;
        }
        return default;
    }

    // Get all parameter names
    public IEnumerable<string> ParameterNames => _parameters.Keys;

    // Get parameter count
    public int Count => _parameters.Count;

    // Convert to DynamicParameters for use with Dapper
    public OracleDynamicParameters ToOracleDynamicParameters()
    {
        var dynamicParams = new OracleDynamicParameters();

        foreach (var param in _parameters.Values)
        {
            dynamicParams.Add(
                param.Name,
                param.Value,
                param.DbType,
                param.Direction,
                param.Size
            );
        }

        return dynamicParams;
    }

    // Convert to DynamicParameters for use with Dapper
    public DynamicParameters ToDynamicParameters()
    {
        var dynamicParams = new DynamicParameters();

        foreach (var param in _parameters.Values)
        {
            dynamicParams.Add(
                param.Name,
                param.Value,
                dbType: null,
                param.Direction,
                param.Size
            );
        }

        return dynamicParams;
    }

    // Implicit conversion to DynamicParameters
    public static implicit operator OracleDynamicParameters(QueryParameterBuilder builder)
    {
        return builder.ToOracleDynamicParameters();
    }

    // Clone the builder
    public QueryParameterBuilder Clone()
    {
        var clone = new QueryParameterBuilder();
        foreach (var param in _parameters.Values)
        {
            clone.Add(param.Name, param.Value, param.DbType, param.Direction, param.Size, param.Precision, param.Scale);
        }
        return clone;
    }

    // Merge with another builder (current builder takes precedence for duplicates)
    public QueryParameterBuilder Merge(QueryParameterBuilder other)
    {
        if (other == null) return this;

        foreach (var param in other._parameters.Values)
        {
            if (!_parameters.ContainsKey(param.Name))
            {
                _parameters[param.Name] = param;
            }
        }

        return this;
    }

    // Override ToString for debugging
    public override string ToString()
    {
        var paramStrings = _parameters.Values.Select(p =>
            $"{p.Name}={p.Value ?? "NULL"} ({p.Direction})");
        return $"ParameterBuilder[{string.Join(", ", paramStrings)}]";
    }

    // Internal parameter info class
    private class ParameterInfo
    {
        public required string Name { get; set; }
        public object? Value { get; set; }
        public OracleMappingType? DbType { get; set; }
        public ParameterDirection Direction { get; set; }
        public int? Size { get; set; }
        public bool? Precision { get; set; }
        public byte? Scale { get; set; }
    }
}

// Extension methods for common scenarios
public static class ParameterBuilderExtensions
{
    // Create a new ParameterBuilder from various sources
    public static QueryParameterBuilder ToParameterBuilder(this object parameters)
    {
        return new QueryParameterBuilder().Add(parameters);
    }

    public static QueryParameterBuilder ToParameterBuilder(this IDictionary<string, object> parameters)
    {
        return new QueryParameterBuilder().Add(parameters);
    }

    public static QueryParameterBuilder ToParameterBuilder(this DynamicParameters parameters)
    {
        return new QueryParameterBuilder().Add(parameters);
    }
}
