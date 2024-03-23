using System.Data.Common;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using HackathonFiap.Lambda.Relatorio.Context;
using Microsoft.EntityFrameworkCore;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace HackathonFiap.Lambda.Relatorio;

public class Function
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly DbConnection _connection;
    private DbCommand? _dbCommand;
    
    /// <summary>
    /// Default constructor. This constructor is used by Lambda to construct the instance. When invoked in a Lambda environment
    /// the AWS credentials will come from the IAM role associated with the function and the AWS region will be set to the
    /// region the Lambda function is executed in.
    /// </summary>
    public Function()
    {
        _jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,

        };

        _connection = MontarConnectionOperacao();
    }


    /// <summary>
    /// This method is called for every Lambda invocation. This method takes in an SQS event object and can be used 
    /// to respond to SQS messages.
    /// </summary>
    /// <param name="evnt">The event for the Lambda function handler to process.</param>
    /// <param name="context">The ILambdaContext that provides methods for logging and describing the Lambda environment.</param>
    /// <returns></returns>
    public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {   
        await _connection.OpenAsync();
        
        foreach (var message in evnt.Records)
        {
            await ProcessMessageAsync(message, context);
        }

        await _connection.CloseAsync();
    }

    private async Task ProcessMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context)
    {   
        context.Logger.LogInformation($"Processed message {message.Body}");
        var periodoDto = JsonSerializer.Deserialize<PeriodoDto>(message.Body, _jsonSerializerOptions);
        
        await using var command = _connection.CreateCommand();

        command.CommandText = "select p.Id, p.Horario from ponto p where p.FuncionarioId = @FuncionarioId and year(p.Horario) = @ano and month(p.Horario) = @mes";
        
        var param = command.CreateParameter();
        param.ParameterName = "@FuncionarioId";
        param.Value = periodoDto.FuncionarioId;
        command.Parameters.Add(param);
                
        param = command.CreateParameter();
        param.ParameterName = "@ano";
        param.Value = periodoDto.Ano;
        command.Parameters.Add(param);
        
        param = command.CreateParameter();
        param.ParameterName = "@mes";
        param.Value = periodoDto.Mes;
        command.Parameters.Add(param);
        
        await command.ExecuteNonQueryAsync();

        var listaPonto = new List<PontoModel>();
        
        var read = await command.ExecuteReaderAsync();
        while (await read.ReadAsync())
        {
            
            listaPonto.Add(new PontoModel
            {
                Id = read.GetGuid(0),
                Horario = read.GetDateTime(1),
                FuncionarioId = periodoDto.FuncionarioId
            });
        }
        await read.CloseAsync();
    }
    
    private DbConnection MontarConnectionOperacao()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionString");
            
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseMySQL(connectionString, op => op.CommandTimeout(600)).Options;
        var db = new DatabaseContext(options);

        return db.Database.GetDbConnection();
    }
}