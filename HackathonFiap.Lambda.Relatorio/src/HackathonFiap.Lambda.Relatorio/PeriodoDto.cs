namespace HackathonFiap.Lambda.Relatorio;

public class PeriodoDto
{
    public Guid RelatorioId { get; set; }
    public Guid FuncionarioId { get; set; }
    public int Ano { get; set; }
    public int Mes { get; set; }
}