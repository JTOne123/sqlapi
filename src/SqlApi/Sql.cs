﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace SqlApi
{
    public sealed class Sql
    {
        public sealed class Command
        {
            private readonly string _connectionString;
            private readonly string _commandText;
            private readonly CommandType _commandType;
            private readonly List<SqlParameter> _parameters;
            private bool _transactional;
            private int? _timeout;

            public Command(string connectionString,
                string commandText,
                CommandType commandType,
                int paramCount = 0)
            {
                this._connectionString = connectionString;
                this._commandText = commandText;
                this._commandType = commandType;
                this._parameters = new List<SqlParameter>(paramCount);
            }

            public Command Param(SqlParameter parameter)
            {
                this._parameters.Add(parameter);
                return this;
            }

            public Command Param(string name, object value)
            {
                return Param(new SqlParameter(name, value ?? DBNull.Value));
            }

            public Command OutParam(string name,
                SqlDbType dbType,
                out SqlParameter parameter)
            {
                return Param(parameter = new SqlParameter(name, dbType) { Direction = ParameterDirection.Output });
            }

            public Command OutParam(string name,
                SqlDbType dbType,
                int size,
                out SqlParameter parameter)
            {
                return Param(parameter = new SqlParameter(name, dbType, size) { Direction = ParameterDirection.Output });
            }

            public Command Params(IEnumerable<SqlParameter> collection)
            {
                _parameters.AddRange(collection);
                return this;
            }

            public Command Transactional()
            {
                this._transactional = true;
                return this;
            }

            public Command Timeout(int value)
            {
                this._timeout = value;
                return this;
            }

            public int Execute()
            {
                var result = 0;

                UsingCommand(c => result = c.ExecuteNonQuery());

                return result;
            }

            public async Task<int> ExecuteAsync()
            {
                var result = 0;

                await UsingCommandAsync(async c => result = await c.ExecuteNonQueryAsync());

                return result;
            }

            public async Task<TResult> QueryOneAsync<TResult>(Func<IDataRecord, TResult> map)
                where TResult : class
                => (await QueryOneAsTupleAsync(map)).result;

            public async Task<(bool found, TResult result)> QueryOneAsTupleAsync<TResult>(Func<IDataRecord, TResult> map)
            {
                var found = false;
                var result = default(TResult);
                
                await UsingCommandAsync(async c =>
                {
                    using (var reader = await c.ExecuteReaderAsync(CommandBehavior.SingleRow))
                    {
                        if (await reader.ReadAsync())
                        {
                            found = true;
                            result = map(reader);
                        }
                    }
                });

                return (found, result);
            }

            public TResult QueryOne<TResult>(Func<IDataRecord, TResult> map)
                where TResult : class
                => QueryOneAsTuple(map).result;

            public (bool found, TResult result) QueryOneAsTuple<TResult>(Func<IDataRecord, TResult> map)
            {
                var found = false;
                var result = default(TResult);

                UsingCommand(c =>
                {
                    using (var reader = c.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        if (reader.Read())
                        {
                            found = true;
                            result = map(reader);
                        }
                    }
                });

                return (found, result);
            }

            public IEnumerable<TElement> Query<TElement>(Func<IDataRecord, TElement> map)
            {
                var result = new List<TElement>();

                Query(r => result.Add(map(r)));

                return result;
            }

            public void Query(Action<IDataRecord> read)
            {
                UsingCommand(c =>
                {
                    using (var reader = c.ExecuteReader(CommandBehavior.SingleResult))
                    {
                        while (reader.Read())
                        {
                            read(reader);
                        }
                    }
                });
            }

            public async Task<IEnumerable<TElement>> QueryAsync<TElement>(Func<IDataRecord, TElement> map)
            {
                var result = new List<TElement>();

                await QueryAsync(r => result.Add(map(r)));

                return result;
            }

            public async Task<Dictionary<TKey, TElement>> QueryAsDictionaryAsync<TKey, TElement>(Func<IDataRecord, TKey> readKey,
                Func<IDataRecord, TElement> readElement)
            {
                var result = new Dictionary<TKey, TElement>();
                await QueryAsync(r => result.Add(readKey(r), readElement(r)));
                return result;
            }

            public Dictionary<TKey, TElement> QueryAsDictionary<TKey, TElement>(Func<IDataRecord, TKey> readKey,
                Func<IDataRecord, TElement> readElement)
            {
                var result = new Dictionary<TKey, TElement>();
                Query(r => result.Add(readKey(r), readElement(r)));
                return result;
            }

            public async Task QueryAsync(Action<IDataRecord> read)
            {
                await UsingCommandAsync(async c =>
                {
                    using (var reader = await c.ExecuteReaderAsync(CommandBehavior.SingleResult))
                    {
                        while (await reader.ReadAsync())
                        {
                            read(reader);
                        }
                    }
                });
            }

            public async Task QueryMultipleAsync(Action<IDataRecord, int> read)
            {
                await UsingCommandAsync(async c =>
                {
                    using (var reader = await c.ExecuteReaderAsync())
                    {
                        var resultIndex = 0;
                        do
                        {
                            while (await reader.ReadAsync())
                            {
                                read(reader, resultIndex);
                            }

                            resultIndex++;
                        } while (await reader.NextResultAsync());
                    }
                });
            }

            private void UsingCommand(Action<SqlCommand> action)
            {
                using (var connection = new SqlConnection(this._connectionString))
                {
                    using (var command = new SqlCommand(this._commandText, connection))
                    {
                        command.Parameters.AddRange(this._parameters.ToArray());
                        command.CommandType = this._commandType;

                        if (this._timeout != null)
                        {
                            command.CommandTimeout = this._timeout.Value;
                        }

                        connection.Open();

                        if (this._transactional)
                        {
                            UsingTransaction(command, action);
                        }
                        else
                        {
                            action(command);
                        }
                    }
                }
            }

            private async Task UsingCommandAsync(Func<SqlCommand, Task> func)
            {
                using (var connection = new SqlConnection(this._connectionString))
                {
                    using (var command = new SqlCommand(this._commandText, connection))
                    {
                        command.Parameters.AddRange(this._parameters.ToArray());
                        command.CommandType = this._commandType;

                        if (this._timeout != null)
                        {
                            command.CommandTimeout = this._timeout.Value;
                        }

                        await connection.OpenAsync();
                        await (this._transactional ? UsingTransactionAsync(command, func) : func(command));
                    }
                }
            }

            private static void UsingTransaction(SqlCommand command,
                Action<SqlCommand> action)
            {
                var transaction = command.Connection.BeginTransaction();
                command.Transaction = transaction;

                try
                {
                    action(command);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
                finally
                {
                    transaction.Dispose();
                }
            }

            private static async Task UsingTransactionAsync(SqlCommand command,
                Func<SqlCommand, Task> func)
            {
                var transaction = command.Connection.BeginTransaction();
                command.Transaction = transaction;

                try
                {
                    await func(command);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
                finally
                {
                    transaction.Dispose();
                }
            }
        }

        private readonly string _connectionString;

        public Sql(string connectionString)
        {
            this._connectionString = connectionString;
        }

        public Command Procedure(string procedureName,
            int paramCount = 0)
        {
            return new Command(this._connectionString,
                procedureName,
                CommandType.StoredProcedure,
                paramCount);
        }

        public Command Text(string sql, int paramCount = 0)
        {
            return new Command(this._connectionString,
                sql,
                CommandType.Text,
                paramCount);
        }
    }
}
