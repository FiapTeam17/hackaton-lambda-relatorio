using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using HackathonFiap.Lambda.Relatorio.Context;
using Microsoft.EntityFrameworkCore;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace HackathonFiap.Lambda.Relatorio;

public class Function
{
    private readonly JsonSerializerOptions _optionsDeserialize;
    private readonly JsonSerializerOptions _optionsSerialize;
    private readonly DbConnection _connection;
    
    /// <summary>
    /// Default constructor. This constructor is used by Lambda to construct the instance. When invoked in a Lambda environment
    /// the AWS credentials will come from the IAM role associated with the function and the AWS region will be set to the
    /// region the Lambda function is executed in.
    /// </summary>
    public Function()
    {
        _optionsSerialize = new JsonSerializerOptions()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };
        
        _optionsDeserialize = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
        var periodoDto = JsonSerializer.Deserialize<PeriodoDto>(message.Body, _optionsDeserialize);
        
        await using var command = _connection.CreateCommand();
        
        command.CommandText = $"select f.Nome, f.Email from funcionario f where f.Id = @FuncionarioId";
        
        var param = command.CreateParameter();
        param.ParameterName = "@FuncionarioId";
        param.Value = periodoDto.FuncionarioId;
        command.Parameters.Add(param);
        await command.ExecuteNonQueryAsync();

        string nome = string.Empty;
        string email = string.Empty;
        
        var read = await command.ExecuteReaderAsync();
        while (await read.ReadAsync())
        {   
            nome = read.GetString(0);
            email = read.GetString(1);
        }
        await read.CloseAsync();
        
        command.Parameters.Clear();
        command.CommandText = $"select p.Id, p.Horario" +
                              $" from ponto p " +
                              $" where p.FuncionarioId = @FuncionarioId and year(p.Horario) = @ano and month(p.Horario) = @mes" +
                              $" order by p.Horario";
        
        param = command.CreateParameter();
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
        
        read = await command.ExecuteReaderAsync();
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

        var html = """
                   <!DOCTYPE html>
                   <html lang="en">
                   <head>
                     <meta charset="UTF-8">
                     <meta name="viewport" content="width=device-width, initial-scale=1.0">
                     <title>Registro de Ponto Mensal</title>
                     <style>
                       body {
                         font-family: Arial, sans-serif;
                         margin: 0;
                         padding: 0;
                       }
                       .container {
                         width: 80%;
                         margin: 20px auto;
                       }
                       h1 {
                         text-align: center;
                       }
                       table {
                         width: 100%;
                         border-collapse: collapse;
                       }
                       th, td {
                         border: 1px solid #ddd;
                         padding: 8px;
                         text-align: center;
                       }
                       th {
                         background-color: #f2f2f2;
                       }
                       .total {
                         font-weight: bold;
                       }
                     </style>
                   </head>
                   <body>
                   <div class="container">
                     <h1>Registro de Ponto [Periodo]</h1>
                     <h2><b>Funcionario: </b>[NomeFuncionario]</h2>
                     <table>
                       <thead>
                       <tr>
                         <th>Data</th>
                         <th>Entrada</th>
                         <th>Saída para o Almoço</th>
                         <th>Retorno do Almoço</th>
                         <th>Saída</th>
                         <th>Total de Horas</th>
                       </tr>
                       </thead>
                       <tbody>
                       [Linhas]
                       <!-- Adicione mais linhas conforme necessário -->
                       </tbody>
                       <tfoot>
                       <tr class="total">
                         <td colspan="5">Total Mensal</td>
                         <td>[Total Horas] horas</td> <!-- Total de horas calculado -->
                       </tr>
                       </tfoot>
                     </table>
                   </div>
                   </body>
                   </html>
                   """;

        html = html.Replace("[Periodo]", $"{periodoDto.Mes.ToString().PadLeft(2,'0')}/{periodoDto.Ano}");
        html = html.Replace("[NomeFuncionario]", nome);
        
        double totalHoras = 0;
        int diaAtual = 0;
        DateTime? entrada1 = null;
        DateTime? entrada2 = null;
        DateTime? saida1 = null;
        DateTime? saida2 = null;
        StringBuilder sb = new StringBuilder();
        foreach (var ponto in listaPonto)
        {
            if (diaAtual != ponto.Horario.Day)
            {
                if (diaAtual != 0)
                {
                    TimeSpan primeiroTurno = TimeSpan.Zero;
                    if (entrada1.HasValue && saida1.HasValue)
                    {
                        primeiroTurno = (DateTime)saida1- (DateTime)entrada1;
                    }
                    
                    TimeSpan segundoTurno = TimeSpan.Zero;
                    if (entrada2.HasValue && saida2.HasValue)
                    {
                        segundoTurno = (DateTime)saida2 - (DateTime)entrada2;
                    }
                    var totalDia = Math.Round(primeiroTurno.TotalHours + segundoTurno.Hours, 0);
            
                    totalHoras += totalDia;
                    
                    var linha = $"""
                                 <tr>
                                   <td>{entrada1:dd/MM/yyyy}</td>
                                   <td>{entrada1:HH:mm:ss}</td>
                                   <td>{saida1:HH:mm:ss}</td>
                                   <td>{entrada2:HH:mm:ss}</td>
                                   <td>{saida2:HH:mm:ss}</td>
                                   <td>{totalDia} horas</td>
                                 </tr>
                                 """;
                    sb.Append(linha);
                    entrada1 = null;
                    entrada2 = null;
                    saida1 = null;
                    saida2 = null;
                }
                diaAtual = ponto.Horario.Day;
            }

            if (!entrada1.HasValue)
            {
                entrada1 = ponto.Horario;
            }else if (!saida1.HasValue)
            {
                saida1 = ponto.Horario;
            }else if (!entrada2.HasValue)
            {
                entrada2 = ponto.Horario;
            }else if (!saida2.HasValue)
            {
                saida2 = ponto.Horario;
            }
        }

        if (listaPonto.Count != 0)
        {
            TimeSpan primeiroTurno = TimeSpan.Zero;
            if (entrada1.HasValue && saida1.HasValue)
            {
                primeiroTurno = (DateTime)saida1- (DateTime)entrada1;
            }
                    
            TimeSpan segundoTurno = TimeSpan.Zero;
            if (entrada2.HasValue && saida2.HasValue)
            {
                segundoTurno = (DateTime)saida2 - (DateTime)entrada2;
            }
            var totalDia = Math.Round(primeiroTurno.TotalHours + segundoTurno.Hours, 0);
            
            totalHoras += totalDia;
                    
            var linha = $"""
                         <tr>
                           <td>{entrada1:dd/MM/yyyy}</td>
                           <td>{entrada1:HH:mm:ss}</td>
                           <td>{saida1:HH:mm:ss}</td>
                           <td>{entrada2:HH:mm:ss}</td>
                           <td>{saida2:HH:mm:ss}</td>
                           <td>{totalDia} horas</td>
                         </tr>
                         """;
            sb.Append(linha);
        }
        
        html = html.Replace("[Linhas]", sb.ToString());
        html = html.Replace("[Total Horas]", totalHoras.ToString(CultureInfo.InvariantCulture));
        
        var solicitacaoEmail = new SolicitacaoEmail
        {
            To = email,
            Subject = $"Registro de Ponto {periodoDto.Mes.ToString().PadLeft(2,'0')}/{periodoDto.Ano}",
            Html = html
        };

        await SolicitarRelatorio(solicitacaoEmail);

        var caminho = await UpalodS3("hackatonfiap", $"{periodoDto.RelatorioId}.html", html);
        
        command.Parameters.Clear();
        command.CommandText = $"update solicitaPonto set Status = @status, CaminhoArquivo = @caminho where Id = @id";
        
        param = command.CreateParameter();
        param.ParameterName = "@status";
        param.Value = "PROCESSADO";
        command.Parameters.Add(param);
        
        param = command.CreateParameter();
        param.ParameterName = "@caminho";
        param.Value = caminho;
        command.Parameters.Add(param);
                
        param = command.CreateParameter();
        param.ParameterName = "@id";
        param.Value = periodoDto.RelatorioId;
        command.Parameters.Add(param);
        
        await command.ExecuteNonQueryAsync();
    }
    
    private DbConnection MontarConnectionOperacao()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionString");
            
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseMySQL(connectionString, op => op.CommandTimeout(600)).Options;
        var db = new DatabaseContext(options);

        return db.Database.GetDbConnection();
    }
    
    public async Task SolicitarRelatorio(SolicitacaoEmail solicitacaoEmail)
    {
        var awsAccessKey = Environment.GetEnvironmentVariable("ClientId");
        var awsSecretKey = Environment.GetEnvironmentVariable("ClientSecret");
        var awsRegion = RegionEndpoint.USEast2;

        // Criando uma instância do cliente SQS
        var sqsClient = new AmazonSQSClient(awsAccessKey, awsSecretKey, awsRegion);

        // Serializando o objeto em formato JSON
        var mensagemJson = JsonSerializer.Serialize(solicitacaoEmail, _optionsSerialize);

        // URL da fila do SQS
        var queueUrl = "https://sqs.us-east-2.amazonaws.com/190197150713/enviar-email.fifo"; // Substitua pelo URL da sua fila

        // Criando uma solicitação de envio de mensagem para a fila
        var sendMessageRequest = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = mensagemJson,
            MessageGroupId = "EnviaEmail"
        };

        var sendMessageResponse = await sqsClient.SendMessageAsync(sendMessageRequest);
    }

    private async Task<string> UpalodS3(string bucket, string key, string content)
    {
        var awsAccessKey = Environment.GetEnvironmentVariable("ClientId");
        var awsSecretKey = Environment.GetEnvironmentVariable("ClientSecret");
        var awsRegion = RegionEndpoint.USEast2;
        
        using var client = new AmazonS3Client(awsAccessKey, awsSecretKey, awsRegion);
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = content
        };
        var response = await client.PutObjectAsync(request);
        
        return $"{bucket}|{key}";
    }
}