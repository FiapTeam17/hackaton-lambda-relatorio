namespace HackathonFiap.Lambda.Relatorio
{
    public record PontoModel
    {
        public Guid Id { get; set; }
        public DateTime Horario { get; set; }
        public Guid FuncionarioId { get; set; }
    }
}
