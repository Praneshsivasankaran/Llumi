export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.hostname !== "tryllumi.com" || url.protocol !== "https:") {
      url.hostname = "tryllumi.com";
      url.protocol = "https:";
      url.port = "";
      return Response.redirect(url.href, 301);
    }
    return env.ASSETS.fetch(request);
  },
};
