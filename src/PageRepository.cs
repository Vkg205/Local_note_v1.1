using LocalNote.Domain.Entities;
using LocalNote.Storage.Services;
using Microsoft.Data.Sqlite;

namespace LocalNote.Storage.Repositories;

public sealed class PageRepository(SqliteDataStore store)
{
    public async Task<IReadOnlyList<Page>> GetActiveAsync(string sectionId, CancellationToken cancellationToken = default)
    {
        var result = new List<Page>();
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, section_id, parent_page_id, title, indent_level, sort_order, is_pinned,
                   is_deleted, local_version, created_at, updated_at
            FROM pages
            WHERE section_id=$sectionId AND is_deleted=0
            ORDER BY is_pinned DESC, sort_order, created_at;
            """;
        command.Parameters.AddWithValue("$sectionId", sectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<Page> CreateAsync(string sectionId, string title, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new Page
        {
            Id = Guid.NewGuid().ToString("N"),
            SectionId = sectionId,
            Title = title,
            SortOrder = await GetNextSortOrderAsync(sectionId, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pages(id, section_id, parent_page_id, title, indent_level, sort_order, is_pinned,
                              is_deleted, local_version, created_at, updated_at)
            VALUES($id, $sectionId, NULL, $title, 0, $sortOrder, 0, 0, 1, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$sectionId", item.SectionId);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$sortOrder", item.SortOrder);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task RenameAsync(string id, string title, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pages SET title=$title, local_version=local_version+1, updated_at=$now WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE pages SET is_pinned=$pinned, local_version=local_version+1, updated_at=$now WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SoftDeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE pages SET is_deleted=1, updated_at=$now WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(string NotebookId, string SectionId, Page Page)?> GetContextAsync(string pageId, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, s.id, p.id, p.section_id, p.parent_page_id, p.title, p.indent_level, p.sort_order, p.is_pinned,
                   p.is_deleted, p.local_version, p.created_at, p.updated_at
            FROM pages p JOIN sections s ON s.id=p.section_id JOIN notebooks n ON n.id=s.notebook_id
            WHERE p.id=$id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", pageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var page = new Page
        {
            Id = reader.GetString(2), SectionId = reader.GetString(3), ParentPageId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Title = reader.GetString(5), IndentLevel = reader.GetInt32(6), SortOrder = reader.GetInt32(7), IsPinned = reader.GetInt32(8) != 0,
            IsDeleted = reader.GetInt32(9) != 0, LocalVersion = reader.GetInt32(10), CreatedAt = DateTimeOffset.Parse(reader.GetString(11)), UpdatedAt = DateTimeOffset.Parse(reader.GetString(12))
        };
        return (reader.GetString(0), reader.GetString(1), page);
    }


    public async Task<IReadOnlyList<PageDestination>> GetDestinationsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PageDestination>();
        await using var connection = store.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id,n.name,s.id,s.name
              FROM sections s JOIN notebooks n ON n.id=s.notebook_id
             WHERE s.is_deleted=0 AND n.is_deleted=0
             ORDER BY n.sort_order,n.name,s.sort_order,s.name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new PageDestination { NotebookId=reader.GetString(0), NotebookName=reader.GetString(1), SectionId=reader.GetString(2), SectionName=reader.GetString(3) });
        return result;
    }

    public async Task MoveAsync(string pageId, string targetSectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var order = connection.CreateCommand(); order.Transaction = (SqliteTransaction)transaction;
        order.CommandText = "SELECT COALESCE(MAX(sort_order),-1)+1 FROM pages WHERE section_id=$section AND is_deleted=0;";
        order.Parameters.AddWithValue("$section",targetSectionId);
        var next = Convert.ToInt32(await order.ExecuteScalarAsync(cancellationToken));
        var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE pages SET section_id=$section,parent_page_id=NULL,indent_level=0,sort_order=$sort,local_version=local_version+1,updated_at=$now WHERE id=$id;";
        command.Parameters.AddWithValue("$section",targetSectionId); command.Parameters.AddWithValue("$sort",next); command.Parameters.AddWithValue("$id",pageId); command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Page> CopyAsync(string sourcePageId, string targetSectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = store.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sourceCmd = connection.CreateCommand(); sourceCmd.Transaction=(SqliteTransaction)transaction;
        sourceCmd.CommandText="SELECT title,is_pinned FROM pages WHERE id=$id AND is_deleted=0 LIMIT 1;"; sourceCmd.Parameters.AddWithValue("$id",sourcePageId);
        string sourceTitle; bool pinned;
        await using (var reader=await sourceCmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("源页面不存在或已删除。");
            sourceTitle=reader.GetString(0); pinned=reader.GetInt32(1)!=0;
        }
        var orderCmd=connection.CreateCommand(); orderCmd.Transaction=(SqliteTransaction)transaction;
        orderCmd.CommandText="SELECT COALESCE(MAX(sort_order),-1)+1 FROM pages WHERE section_id=$section AND is_deleted=0;"; orderCmd.Parameters.AddWithValue("$section",targetSectionId);
        var sort=Convert.ToInt32(await orderCmd.ExecuteScalarAsync(cancellationToken));
        var now=DateTimeOffset.UtcNow; var newPageId=Guid.NewGuid().ToString("N"); var newTitle=$"{sourceTitle} - 副本";
        var insertPage=connection.CreateCommand(); insertPage.Transaction=(SqliteTransaction)transaction;
        insertPage.CommandText="""
            INSERT INTO pages(id,section_id,parent_page_id,title,indent_level,sort_order,is_pinned,is_deleted,local_version,created_at,updated_at)
            VALUES($id,$section,NULL,$title,0,$sort,$pinned,0,1,$now,$now);
            """;
        insertPage.Parameters.AddWithValue("$id",newPageId); insertPage.Parameters.AddWithValue("$section",targetSectionId); insertPage.Parameters.AddWithValue("$title",newTitle);
        insertPage.Parameters.AddWithValue("$sort",sort); insertPage.Parameters.AddWithValue("$pinned",pinned?1:0); insertPage.Parameters.AddWithValue("$now",now.ToString("O"));
        await insertPage.ExecuteNonQueryAsync(cancellationToken);

        var objectCmd=connection.CreateCommand(); objectCmd.Transaction=(SqliteTransaction)transaction;
        objectCmd.CommandText="SELECT id,type,x,y,width,height,z_index,payload,style_json,is_todo,todo_completed,is_important FROM content_objects WHERE page_id=$page ORDER BY z_index,created_at;";
        objectCmd.Parameters.AddWithValue("$page",sourcePageId);
        var rows=new List<(string OldId,string Type,double X,double Y,double W,double H,int Z,string Payload,string Style,int Todo,int Done,int Important)>();
        await using (var reader=await objectCmd.ExecuteReaderAsync(cancellationToken))
        {
            while(await reader.ReadAsync(cancellationToken)) rows.Add((reader.GetString(0),reader.GetString(1),reader.GetDouble(2),reader.GetDouble(3),reader.GetDouble(4),reader.GetDouble(5),reader.GetInt32(6),reader.GetString(7),reader.GetString(8),reader.GetInt32(9),reader.GetInt32(10),reader.GetInt32(11)));
        }
        foreach(var row in rows)
        {
            var newObjectId=Guid.NewGuid().ToString("N");
            var ins=connection.CreateCommand(); ins.Transaction=(SqliteTransaction)transaction;
            ins.CommandText="""
                INSERT INTO content_objects(id,page_id,type,x,y,width,height,z_index,payload,style_json,is_todo,todo_completed,is_important,local_version,created_at,updated_at)
                VALUES($id,$page,$type,$x,$y,$w,$h,$z,$payload,$style,$todo,$done,$important,1,$now,$now);
                """;
            ins.Parameters.AddWithValue("$id",newObjectId); ins.Parameters.AddWithValue("$page",newPageId); ins.Parameters.AddWithValue("$type",row.Type);
            ins.Parameters.AddWithValue("$x",row.X); ins.Parameters.AddWithValue("$y",row.Y); ins.Parameters.AddWithValue("$w",row.W); ins.Parameters.AddWithValue("$h",row.H); ins.Parameters.AddWithValue("$z",row.Z);
            ins.Parameters.AddWithValue("$payload",row.Payload); ins.Parameters.AddWithValue("$style",row.Style); ins.Parameters.AddWithValue("$todo",row.Todo); ins.Parameters.AddWithValue("$done",row.Done); ins.Parameters.AddWithValue("$important",row.Important); ins.Parameters.AddWithValue("$now",now.ToString("O"));
            await ins.ExecuteNonQueryAsync(cancellationToken);
            var idx=connection.CreateCommand(); idx.Transaction=(SqliteTransaction)transaction;
            idx.CommandText="INSERT INTO search_fts(object_id,page_id,source,text) SELECT $newId,$newPage,source,text FROM search_fts WHERE object_id=$oldId;";
            idx.Parameters.AddWithValue("$newId",newObjectId); idx.Parameters.AddWithValue("$newPage",newPageId); idx.Parameters.AddWithValue("$oldId",row.OldId); await idx.ExecuteNonQueryAsync(cancellationToken);
        }
        var ink=connection.CreateCommand(); ink.Transaction=(SqliteTransaction)transaction;
        ink.CommandText="INSERT INTO ink_layers(page_id,isf_data,updated_at) SELECT $newPage,isf_data,$now FROM ink_layers WHERE page_id=$source;";
        ink.Parameters.AddWithValue("$newPage",newPageId); ink.Parameters.AddWithValue("$source",sourcePageId); ink.Parameters.AddWithValue("$now",now.ToString("O")); await ink.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Page { Id=newPageId,SectionId=targetSectionId,Title=newTitle,SortOrder=sort,IsPinned=pinned,CreatedAt=now,UpdatedAt=now };
    }

    private async Task<int> GetNextSortOrderAsync(string sectionId, CancellationToken cancellationToken)
    {
        await using var connection = store.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM pages WHERE section_id=$sectionId;";
        command.Parameters.AddWithValue("$sectionId", sectionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static Page Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        SectionId = reader.GetString(1),
        ParentPageId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Title = reader.GetString(3),
        IndentLevel = reader.GetInt32(4),
        SortOrder = reader.GetInt32(5),
        IsPinned = reader.GetInt32(6) != 0,
        IsDeleted = reader.GetInt32(7) != 0,
        LocalVersion = reader.GetInt32(8),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(9)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
    };
}
