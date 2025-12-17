using System;
using System.Collections.Generic;
using AMMS.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.DBContext;

public partial class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<bom> boms { get; set; }

    public virtual DbSet<customer> customers { get; set; }

    public virtual DbSet<delivery> deliveries { get; set; }

    public virtual DbSet<material> materials { get; set; }

    public virtual DbSet<order> orders { get; set; }

    public virtual DbSet<order_item> order_items { get; set; }

    public virtual DbSet<order_request> order_requests { get; set; }

    public virtual DbSet<product_type> product_types { get; set; }

    public virtual DbSet<product_type_process> product_type_processes { get; set; }

    public virtual DbSet<production> productions { get; set; }

    public virtual DbSet<purchase> purchases { get; set; }

    public virtual DbSet<purchase_item> purchase_items { get; set; }

    public virtual DbSet<quote> quotes { get; set; }

    public virtual DbSet<role> roles { get; set; }

    public virtual DbSet<stock_move> stock_moves { get; set; }

    public virtual DbSet<supplier> suppliers { get; set; }

    public virtual DbSet<task> tasks { get; set; }

    public virtual DbSet<task_log> task_logs { get; set; }

    public virtual DbSet<user> users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<bom>(entity =>
        {
            entity.HasKey(e => e.bom_id).HasName("boms_pkey");

            entity.Property(e => e.qty_per_product).HasPrecision(10, 4);
            entity.Property(e => e.wastage_percent)
                .HasPrecision(5, 2)
                .HasDefaultValueSql("5.0");

            entity.HasOne(d => d.material).WithMany(p => p.boms)
                .HasForeignKey(d => d.material_id)
                .HasConstraintName("boms_material_id_fkey");

            entity.HasOne(d => d.order_item).WithMany(p => p.boms)
                .HasForeignKey(d => d.order_item_id)
                .HasConstraintName("boms_order_item_id_fkey");
        });

        modelBuilder.Entity<customer>(entity =>
        {
            entity.HasKey(e => e.customer_id).HasName("customers_pkey");

            entity.Property(e => e.company_name).HasMaxLength(150);
            entity.Property(e => e.contact_name).HasMaxLength(100);
            entity.Property(e => e.email).HasMaxLength(100);
            entity.Property(e => e.phone).HasMaxLength(20);

            entity.HasOne(d => d.user).WithMany(p => p.customers)
                .HasForeignKey(d => d.user_id)
                .HasConstraintName("customers_user_id_fkey");
        });

        modelBuilder.Entity<delivery>(entity =>
        {
            entity.HasKey(e => e.delivery_id).HasName("deliveries_pkey");

            entity.Property(e => e.carrier).HasMaxLength(100);
            entity.Property(e => e.ship_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Ready'::character varying");
            entity.Property(e => e.tracking_code).HasMaxLength(50);

            entity.HasOne(d => d.order).WithMany(p => p.deliveries)
                .HasForeignKey(d => d.order_id)
                .HasConstraintName("deliveries_order_id_fkey");
        });

        modelBuilder.Entity<material>(entity =>
        {
            entity.HasKey(e => e.material_id).HasName("materials_pkey");

            entity.HasIndex(e => e.code, "materials_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(50);
            entity.Property(e => e.cost_price).HasPrecision(15, 2);
            entity.Property(e => e.min_stock)
                .HasPrecision(10, 2)
                .HasDefaultValueSql("100");
            entity.Property(e => e.name).HasMaxLength(150);
            entity.Property(e => e.stock_qty)
                .HasPrecision(10, 2)
                .HasDefaultValueSql("0");
            entity.Property(e => e.unit).HasMaxLength(20);
        });

        modelBuilder.Entity<order>(entity =>
        {
            entity.HasKey(e => e.order_id).HasName("orders_pkey");

            entity.HasIndex(e => e.code, "orders_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.delivery_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.order_date)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.payment_status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Unpaid'::character varying");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'New'::character varying");
            entity.Property(e => e.total_amount).HasPrecision(15, 2);

            entity.HasOne(d => d.consultant).WithMany(p => p.orders)
                .HasForeignKey(d => d.consultant_id)
                .HasConstraintName("orders_consultant_id_fkey");

            entity.HasOne(d => d.customer).WithMany(p => p.orders)
                .HasForeignKey(d => d.customer_id)
                .HasConstraintName("orders_customer_id_fkey");

            entity.HasOne(d => d.quote).WithMany(p => p.orders)
                .HasForeignKey(d => d.quote_id)
                .HasConstraintName("orders_quote_id_fkey");
        });

        modelBuilder.Entity<order_item>(entity =>
        {
            entity.HasKey(e => e.item_id).HasName("order_items_pkey");

            entity.Property(e => e.colors).HasMaxLength(50);
            entity.Property(e => e.finished_size).HasMaxLength(50);
            entity.Property(e => e.paper_type).HasMaxLength(100);
            entity.Property(e => e.print_size).HasMaxLength(50);
            entity.Property(e => e.product_name).HasMaxLength(200);

            entity.HasOne(d => d.order).WithMany(p => p.order_items)
                .HasForeignKey(d => d.order_id)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("order_items_order_id_fkey");

            entity.HasOne(d => d.product_type).WithMany(p => p.order_items)
                .HasForeignKey(d => d.product_type_id)
                .HasConstraintName("order_items_product_type_id_fkey");
        });

        modelBuilder.Entity<order_request>(entity =>
        {
            entity.HasKey(e => e.order_request_id).HasName("order_request_pkey");

            entity.ToTable("order_request", "AMMS_DB");

            entity.Property(e => e.customer_email).HasMaxLength(100);
            entity.Property(e => e.customer_name).HasMaxLength(100);
            entity.Property(e => e.customer_phone).HasMaxLength(20);
            entity.Property(e => e.delivery_date).HasColumnType("timestamp with time zone");
            entity.Property(e => e.order_request_date).HasColumnType("timestamp with time zone");

            entity.Property(e => e.product_name).HasMaxLength(200);
        });

        modelBuilder.Entity<product_type>(entity =>
        {
            entity.HasKey(e => e.product_type_id).HasName("product_types_pkey");

            entity.HasIndex(e => e.code, "product_types_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.is_active).HasDefaultValue(true);
            entity.Property(e => e.name).HasMaxLength(100);
        });

        modelBuilder.Entity<product_type_process>(entity =>
        {
            entity.HasKey(e => e.process_id).HasName("product_type_process_pkey");

            entity.ToTable("product_type_process");

            entity.Property(e => e.is_active).HasDefaultValue(true);
            entity.Property(e => e.machine).HasMaxLength(50);
            entity.Property(e => e.process_name).HasMaxLength(100);

            entity.HasOne(d => d.product_type).WithMany(p => p.product_type_processes)
                .HasForeignKey(d => d.product_type_id)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("product_type_process_product_type_id_fkey");
        });

        modelBuilder.Entity<production>(entity =>
        {
            entity.HasKey(e => e.prod_id).HasName("productions_pkey");

            entity.HasIndex(e => e.code, "productions_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.end_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.start_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Planned'::character varying");

            entity.HasOne(d => d.manager).WithMany(p => p.productions)
                .HasForeignKey(d => d.manager_id)
                .HasConstraintName("productions_manager_id_fkey");

            entity.HasOne(d => d.order).WithMany(p => p.productions)
                .HasForeignKey(d => d.order_id)
                .HasConstraintName("productions_order_id_fkey");

            entity.HasOne(d => d.product_type).WithMany(p => p.productions)
                .HasForeignKey(d => d.product_type_id)
                .HasConstraintName("productions_product_type_id_fkey");
        });

        modelBuilder.Entity<purchase>(entity =>
        {
            entity.HasKey(e => e.purchase_id).HasName("purchases_pkey");

            entity.HasIndex(e => e.code, "purchases_code_key").IsUnique();

            entity.Property(e => e.code).HasMaxLength(20);
            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.eta_date).HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Pending'::character varying");

            entity.HasOne(d => d.created_byNavigation).WithMany(p => p.purchases)
                .HasForeignKey(d => d.created_by)
                .HasConstraintName("purchases_created_by_fkey");

            entity.HasOne(d => d.supplier).WithMany(p => p.purchases)
                .HasForeignKey(d => d.supplier_id)
                .HasConstraintName("purchases_supplier_id_fkey");
        });

        modelBuilder.Entity<purchase_item>(entity =>
        {
            entity.HasKey(e => e.id).HasName("purchase_items_pkey");

            entity.Property(e => e.price).HasPrecision(15, 2);
            entity.Property(e => e.qty_ordered).HasPrecision(10, 2);

            entity.HasOne(d => d.material).WithMany(p => p.purchase_items)
                .HasForeignKey(d => d.material_id)
                .HasConstraintName("purchase_items_material_id_fkey");

            entity.HasOne(d => d.purchase).WithMany(p => p.purchase_items)
                .HasForeignKey(d => d.purchase_id)
                .HasConstraintName("purchase_items_purchase_id_fkey");
        });

        modelBuilder.Entity<quote>(entity =>
        {
            entity.HasKey(e => e.quote_id).HasName("quotes_pkey");

            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Draft'::character varying");
            entity.Property(e => e.total_amount).HasPrecision(15, 2);

            entity.HasOne(d => d.consultant).WithMany(p => p.quotes)
                .HasForeignKey(d => d.consultant_id)
                .HasConstraintName("quotes_consultant_id_fkey");

            entity.HasOne(d => d.customer).WithMany(p => p.quotes)
                .HasForeignKey(d => d.customer_id)
                .HasConstraintName("quotes_customer_id_fkey");
        });

        modelBuilder.Entity<role>(entity =>
        {
            entity.HasKey(e => e.role_id).HasName("roles_pkey");

            entity.HasIndex(e => e.role_name, "roles_role_name_key").IsUnique();

            entity.Property(e => e.role_name).HasMaxLength(50);
        });

        modelBuilder.Entity<stock_move>(entity =>
        {
            entity.HasKey(e => e.move_id).HasName("stock_moves_pkey");

            entity.Property(e => e.move_date)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.qty).HasPrecision(10, 2);
            entity.Property(e => e.ref_doc).HasMaxLength(50);
            entity.Property(e => e.type).HasMaxLength(10);

            entity.HasOne(d => d.material).WithMany(p => p.stock_moves)
                .HasForeignKey(d => d.material_id)
                .HasConstraintName("stock_moves_material_id_fkey");

            entity.HasOne(d => d.user).WithMany(p => p.stock_moves)
                .HasForeignKey(d => d.user_id)
                .HasConstraintName("stock_moves_user_id_fkey");
        });

        modelBuilder.Entity<supplier>(entity =>
        {
            entity.HasKey(e => e.supplier_id).HasName("suppliers_pkey");

            entity.Property(e => e.contact_person).HasMaxLength(100);
            entity.Property(e => e.email).HasMaxLength(100);
            entity.Property(e => e.main_material_type).HasMaxLength(50);
            entity.Property(e => e.name).HasMaxLength(150);
            entity.Property(e => e.phone).HasMaxLength(20);
        });

        modelBuilder.Entity<task>(entity =>
        {
            entity.HasKey(e => e.task_id).HasName("tasks_pkey");

            entity.Property(e => e.end_time).HasColumnType("timestamp without time zone");
            entity.Property(e => e.machine).HasMaxLength(50);
            entity.Property(e => e.name).HasMaxLength(100);
            entity.Property(e => e.start_time).HasColumnType("timestamp without time zone");
            entity.Property(e => e.status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Unassigned'::character varying");

            entity.HasOne(d => d.assigned_toNavigation).WithMany(p => p.tasks)
                .HasForeignKey(d => d.assigned_to)
                .HasConstraintName("tasks_assigned_to_fkey");

            entity.HasOne(d => d.process).WithMany(p => p.tasks)
                .HasForeignKey(d => d.process_id)
                .HasConstraintName("tasks_process_id_fkey");

            entity.HasOne(d => d.prod).WithMany(p => p.tasks)
                .HasForeignKey(d => d.prod_id)
                .HasConstraintName("tasks_prod_id_fkey");
        });

        modelBuilder.Entity<task_log>(entity =>
        {
            entity.HasKey(e => e.log_id).HasName("task_logs_pkey");

            entity.Property(e => e.action_type)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Finish'::character varying");
            entity.Property(e => e.log_time)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.qty_bad).HasDefaultValue(0);
            entity.Property(e => e.qty_good).HasDefaultValue(0);
            entity.Property(e => e.scanned_code).HasMaxLength(100);
            entity.Property(e => e.scanner_id).HasMaxLength(50);

            entity.HasOne(d => d._operator).WithMany(p => p.task_logs)
                .HasForeignKey(d => d.operator_id)
                .HasConstraintName("task_logs_operator_id_fkey");

            entity.HasOne(d => d.task).WithMany(p => p.task_logs)
                .HasForeignKey(d => d.task_id)
                .HasConstraintName("task_logs_task_id_fkey");
        });

        modelBuilder.Entity<user>(entity =>
        {
            entity.HasKey(e => e.user_id).HasName("users_pkey");

            entity.HasIndex(e => e.username, "users_username_key").IsUnique();

            entity.Property(e => e.created_at)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.full_name).HasMaxLength(100);
            entity.Property(e => e.is_active).HasDefaultValue(true);
            entity.Property(e => e.password_hash).HasMaxLength(255);
            entity.Property(e => e.username).HasMaxLength(50);

            entity.HasOne(d => d.role).WithMany(p => p.users)
                .HasForeignKey(d => d.role_id)
                .HasConstraintName("users_role_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
