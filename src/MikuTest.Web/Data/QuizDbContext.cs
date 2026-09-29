using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Models;

namespace MikuTest.Web.Data;

public class QuizDbContext(DbContextOptions<QuizDbContext> options) : DbContext(options)
{
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionGroup> QuestionGroups => Set<QuestionGroup>();
    public DbSet<QuizQuestion> QuizQuestions => Set<QuizQuestion>();
    public DbSet<Choice> Choices => Set<Choice>();
    public DbSet<QuestionMedia> QuestionMedia => Set<QuestionMedia>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<KnowledgeDomain> KnowledgeDomains => Set<KnowledgeDomain>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<RandomPaper> RandomPapers => Set<RandomPaper>();
    public DbSet<RandomPaperQuestion> RandomPaperQuestions => Set<RandomPaperQuestion>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Question>()
            .HasOne(x => x.Group)
            .WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Restrict);
        m.Entity<QuizQuestion>().HasKey(x => new { x.QuizId, x.QuestionId });
        m.Entity<Quiz>()
            .HasMany(x => x.Items)
            .WithOne(x => x.Quiz)
            .HasForeignKey(x => x.QuizId)
            .OnDelete(DeleteBehavior.Cascade);
        m.Entity<QuizQuestion>()
            .HasOne(x => x.Question)
            .WithMany()
            .HasForeignKey(x => x.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
        m.Entity<QuizQuestion>().HasIndex(x => new { x.QuizId, x.Order }).IsUnique();
        m.Entity<Question>().HasMany(x => x.Choices).WithOne().HasForeignKey(x => x.QuestionId);
        m.Entity<Question>().HasMany(x => x.Media).WithOne().HasForeignKey(x => x.QuestionId);
        m.Entity<Question>().HasMany(x => x.Tags).WithMany(x => x.Questions).UsingEntity("QuestionTags");
        m.Entity<Question>()
            .HasMany(x => x.Domains)
            .WithMany(x => x.Questions)
            .UsingEntity("QuestionDomains");
        m.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        m.Entity<KnowledgeDomain>().HasIndex(x => x.Name).IsUnique();
        m.Entity<KnowledgeDomain>().HasIndex(x => x.Order).IsUnique();
        m.Entity<Attempt>().HasMany(x => x.Answers).WithOne().HasForeignKey(x => x.AttemptId);
        m.Entity<Answer>().HasIndex(x => new { x.AttemptId, x.QuestionId }).IsUnique();
        m.Entity<Attempt>()
            .HasOne<Quiz>()
            .WithMany()
            .HasForeignKey(x => x.QuizId)
            .OnDelete(DeleteBehavior.Restrict);
        m.Entity<Attempt>().HasIndex(x => new { x.UserId, x.SubmittedAtUtc });
        m.Entity<RandomPaperQuestion>().HasKey(x => new { x.RandomPaperId, x.QuestionId });
        m.Entity<RandomPaper>().HasMany(x => x.Questions).WithOne().HasForeignKey(x => x.RandomPaperId);
        m.Entity<RandomPaperQuestion>()
            .HasOne(x => x.Question)
            .WithMany()
            .HasForeignKey(x => x.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
