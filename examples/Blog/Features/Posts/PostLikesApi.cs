using Microsoft.AspNetCore.Http.HttpResults;

namespace Blog.Features.Posts;

/// <summary>
/// Likes: a plain JSON API for the live count (read with TanStack Query), and a server action to like a post.
/// </summary>
public static class PostLikesApi
{
    public sealed record LikesResponse(string Slug, int Likes);

    /// <summary>An empty JSON body; requiring application/json keeps cross-site form posts out.</summary>
    public sealed record LikeRequest;

    public sealed record LikeInput(string Slug);

    public static void MapPostLikesApi(this IEndpointRouteBuilder app)
    {
        var likes = app.MapGroup("/api/posts/{slug}/likes");

        likes.MapGet("/", Results<Ok<LikesResponse>, NotFound> (string slug, PostStore store) =>
            store.Find(slug) is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(new LikesResponse(slug, store.LikesOf(slug))));

        likes.MapPost("/", Results<Ok<LikesResponse>, NotFound> (string slug, LikeRequest request, PostStore store) =>
            store.Find(slug) is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(new LikesResponse(slug, store.Like(slug))));

        // Called from LikeButton.tsx as likePost({ slug }). The new count goes straight into the client's query cache.
        app.MapAction("posts.like", (LikeInput input, PostStore store) =>
        {
            if (store.Find(input.Slug) is null)
            {
                return ServerAction.Invalid("slug", "There is no such post.");
            }

            var likes = new LikesResponse(input.Slug, store.Like(input.Slug));
            return ServerAction.Ok(likes).WithQuery(["likes", input.Slug], likes);
        });
    }
}
